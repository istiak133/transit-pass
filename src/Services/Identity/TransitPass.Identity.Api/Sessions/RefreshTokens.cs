using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Identity.Api.Data;

namespace TransitPass.Identity.Api.Sessions;

/// <summary>A new refresh token as the client sees it, and when it stops working.</summary>
public sealed record IssuedRefreshToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>A successful rotation: whose session it is, and the token that replaces the presented one.</summary>
public sealed record RotatedRefreshToken(Guid UserId, Guid FamilyId, IssuedRefreshToken Next);

/// <summary>
/// Refresh tokens with rotation and family revoke.
/// <list type="bullet">
/// <item>Token: 32 bytes from the OS CSPRNG (256 bits), sent as 43 base64url characters. Opaque: it carries no
/// data, it is only a lookup key. The database keeps SHA-256(bytes), never the token.</item>
/// <item>Rotation: a token works once. Refresh marks it used and issues its successor in the same family.</item>
/// <item>Reuse: a used token presented again means two parties hold it (one is a thief). Nobody can tell which,
/// so the whole family is revoked, including the newest token, and both must log in again.</item>
/// <item>Concurrency: every change to a family first locks the family's root row (SELECT … FOR UPDATE), so
/// rotations, reuse revokes and logouts of one family run one at a time. Different families never wait.</item>
/// </list>
/// </summary>
internal static class RefreshTokens
{
    /// <summary>Random bytes per token: 256 bits, so guessing one is hopeless.</summary>
    internal const int TokenBytes = 32;

    /// <summary>Length of the base64url text of <see cref="TokenBytes"/> bytes, without padding.</summary>
    internal const int TokenChars = 43;

    internal static readonly Error InvalidToken =
        Error.Unauthorized("identity.invalid_refresh_token", "The refresh token is not valid. Log in again.");

    internal static readonly Error ExpiredToken =
        Error.Unauthorized("identity.refresh_token_expired", "The refresh token has expired. Log in again.");

    internal static readonly Error ReusedToken =
        Error.Unauthorized("identity.refresh_token_reused",
            "This refresh token was already used. The session has been ended for safety. Log in again.");

    /// <summary>
    /// SHA-256 of the token's bytes, or false when the text can't be one of our tokens (wrong length, not base64url).
    /// A plain fast hash is right here: the input is 256 random bits, so there is nothing to brute-force,
    /// unlike a password (which is why passwords get slow PBKDF2 instead).
    /// </summary>
    internal static bool TryHash(string? token, out byte[] hash)
    {
        hash = [];
        if (token is null || token.Length != TokenChars)
        {
            return false;
        }

        // The OperationStatus overload reports bad characters as InvalidData instead of throwing.
        Span<byte> raw = stackalloc byte[TokenBytes];
        OperationStatus status = Base64Url.DecodeFromChars(token, raw, out int consumed, out int written);
        if (status != OperationStatus.Done || consumed != TokenChars || written != TokenBytes)
        {
            return false;
        }

        hash = SHA256.HashData(raw);
        return true;
    }

    /// <summary>A fresh token and the row that remembers it (hash only). The caller adds the row.</summary>
    private static (IssuedRefreshToken Issued, RefreshToken Row) Create(
        Guid userId, Guid? familyId, DateTimeOffset now, TimeSpan lifetime)
    {
        Span<byte> raw = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(raw);
        Guid id = Guid.CreateVersion7(now);
        var row = new RefreshToken
        {
            Id = id,
            UserId = userId,
            FamilyId = familyId ?? id, // the first token of a login is the family root
            TokenHash = SHA256.HashData(raw),
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
        return (new IssuedRefreshToken(Base64Url.EncodeToString(raw), row.ExpiresAt), row);
    }

    /// <summary>Login: start a new family with its root token.</summary>
    internal static async Task<IssuedRefreshToken> StartFamilyAsync(
        IdentityDbContext db, Guid userId, DateTimeOffset now, TimeSpan lifetime, CancellationToken ct)
    {
        var (issued, row) = Create(userId, familyId: null, now, lifetime);
        db.RefreshTokens.Add(row);
        await db.SaveChangesAsync(ct);
        return issued;
    }

    /// <summary>
    /// Exchange a token for its successor. One transaction:
    ///   1. find the row by hash (unique index), outside the transaction: unknown → 401 at once, no lock taken
    ///   2. lock the family root row FOR UPDATE: this family's rotations, revokes and logouts now wait for us
    ///   3. read the presented row again (now the latest committed state) and decide:
    ///      revoked → 401 invalid · used → reuse: revoke the family, COMMIT, 401 reused · expired → 401 expired
    ///   4. insert the successor, then the guarded UPDATE … SET used_at WHERE used_at IS NULL AND revoked_at IS NULL
    /// </summary>
    internal static async Task<Result<RotatedRefreshToken>> RotateAsync(
        IdentityDbContext db, byte[] hash, DateTimeOffset now, TimeSpan lifetime, ILogger logger, CancellationToken ct)
    {
        var found = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => new { t.Id, t.FamilyId })
            .FirstOrDefaultAsync(ct);
        if (found is null)
        {
            return InvalidToken;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockFamilyAsync(db, found.FamilyId, ct);

        RefreshToken current = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == found.Id, ct);
        if (current.RevokedAt is not null)
        {
            return InvalidToken; // logged out, or already revoked by an earlier reuse
        }

        if (current.UsedAt is not null)
        {
            // Theft signal. Checked before expiry: an old used token can still have a live descendant.
            int revoked = await RevokeFamilyRowsAsync(db, current.FamilyId, now, ct);
            await tx.CommitAsync(ct);
            logger.LogWarning(
                "Refresh token reuse: family {FamilyId} of user {UserId} revoked ({Revoked} tokens)",
                current.FamilyId, current.UserId, revoked);
            return ReusedToken;
        }

        if (current.ExpiresAt <= now)
        {
            return ExpiredToken; // unused, so it is the newest of its family: nothing else to revoke
        }

        var (issued, next) = Create(current.UserId, current.FamilyId, now, lifetime);
        db.RefreshTokens.Add(next);
        await db.SaveChangesAsync(ct); // successor first: replaced_by points at it (fk_refresh_tokens_replaced_by)

        // The compare-and-set. Under the family lock it always matches; it stays as the rule written in SQL,
        // so even a writer that skipped the lock could never mark one token used twice.
        int marked = await db.RefreshTokens
            .Where(t => t.Id == current.Id && t.UsedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now).SetProperty(t => t.ReplacedBy, next.Id), ct);
        if (marked != 1)
        {
            await tx.RollbackAsync(ct);
            return InvalidToken;
        }

        await tx.CommitAsync(ct);
        return new RotatedRefreshToken(current.UserId, current.FamilyId, issued);
    }

    /// <summary>
    /// Logout: revoke the token's whole family (this device's session). Idempotent: a family that is already
    /// revoked answers success again. Works with a used or expired token too: any token of the login ends it.
    /// </summary>
    internal static async Task<Result<int>> RevokeFamilyAsync(
        IdentityDbContext db, byte[] hash, DateTimeOffset now, CancellationToken ct)
    {
        Guid? familyId = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => (Guid?)t.FamilyId)
            .FirstOrDefaultAsync(ct);
        if (familyId is null)
        {
            return InvalidToken;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockFamilyAsync(db, familyId.Value, ct);
        int revoked = await RevokeFamilyRowsAsync(db, familyId.Value, now, ct);
        await tx.CommitAsync(ct);
        return revoked;
    }

    /// <summary>
    /// The family lock: the root row, FOR UPDATE, held until COMMIT. Without it, a revoke could miss a successor
    /// that a parallel refresh inserts after the revoke's UPDATE took its snapshot: that new row would stay alive.
    /// </summary>
    private static Task<List<Guid>> LockFamilyAsync(IdentityDbContext db, Guid familyId, CancellationToken ct) =>
        db.Database
            .SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM refresh_tokens WHERE id = {familyId} FOR UPDATE
                """)
            .ToListAsync(ct);

    /// <summary>One indexed UPDATE (ix_refresh_tokens_family_id): every not-yet-revoked token of the family.</summary>
    private static Task<int> RevokeFamilyRowsAsync(IdentityDbContext db, Guid familyId, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
}
