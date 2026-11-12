using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TransitPass.BuildingBlocks.Auth;
using TransitPass.BuildingBlocks.Http;
using TransitPass.BuildingBlocks.Pagination;
using TransitPass.Core.Api.Data;
using TransitPass.Core.Api.Ledger;

namespace TransitPass.Core.Api.Wallets;

/// <summary>Wallet endpoints under /api/v1/wallet. Every route here requires a valid access token.</summary>
public static class WalletEndpoints
{
    internal const string Currency = "BDT";

    /// <summary>Maps the wallet routes.</summary>
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/wallet").RequireAuthorization();

        // GET /api/v1/wallet/me → 200 { walletId, userId, balancePoisha, currency }.
        // "me" comes from the token's sub (Phase 6), never from the URL, so nobody can read another wallet.
        group.MapGet("/me", async (ClaimsPrincipal user, CoreDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            Wallet wallet = await GetOrCreateAsync(db, user.GetUserId(), clock.GetUtcNow(), ct);
            return TypedResults.Ok(ToResponse(wallet));
        });

        // GET /api/v1/wallet/me/transactions?after=<cursor>&limit=20 → newest first, keyset paged (Phase 0).
        group.MapGet("/me/transactions", async (
            ClaimsPrincipal user, CoreDbContext db, string? after, int? limit, CancellationToken ct) =>
        {
            var page = PageRequest.Create(after, limit);
            Guid? afterId = null;
            if (page.After is not null)
            {
                var decoded = Cursor.Decode(page.After);
                if (!decoded.IsSuccess)
                {
                    return decoded.Error.ToProblem();
                }

                afterId = decoded.Value;
            }

            return TypedResults.Ok(await ListTransactionsAsync(db, user.GetUserId(), page, afterId, ct));
        });

        return app;
    }

    /// <summary>
    /// Finds the user's wallet, creating it on first use. Safe under races: two first calls at once both
    /// run INSERT … ON CONFLICT DO NOTHING; the UNIQUE(user_id) index lets exactly one row in, and both
    /// then read that same row. No "check, then insert" gap.
    /// </summary>
    internal static async Task<Wallet> GetOrCreateAsync(CoreDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        Wallet? wallet = await db.Wallets.AsNoTracking().SingleOrDefaultAsync(w => w.UserId == userId, ct);
        if (wallet is not null)
        {
            return wallet; // the common path: one indexed lookup
        }

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO wallets (id, user_id, balance_poisha, created_at)
            VALUES ({Guid.CreateVersion7(now)}, {userId}, 0, {now})
            ON CONFLICT (user_id) DO NOTHING
            """, ct);

        return await db.Wallets.AsNoTracking().SingleAsync(w => w.UserId == userId, ct);
    }

    /// <summary>
    /// One page of history: WHERE wallet_id = @w AND id &lt; @after ORDER BY id DESC LIMIT n+1.
    /// The (wallet_id, id) index answers it in O(log n + n_page), however deep the page is.
    /// A user with no wallet yet simply has an empty history; reading it does not create one.
    /// </summary>
    internal static async Task<CursorPage<LedgerEntryResponse>> ListTransactionsAsync(
        CoreDbContext db, Guid userId, PageRequest page, Guid? afterId, CancellationToken ct)
    {
        // Query 1: user → wallet id, via ux_wallets_user_id. O(log n).
        Guid? walletId = await db.Wallets.Where(w => w.UserId == userId).Select(w => (Guid?)w.Id).SingleOrDefaultAsync(ct);
        if (walletId is null)
        {
            return new CursorPage<LedgerEntryResponse>([], null);
        }

        // Query 2: an index range scan on ix_ledger_entries_wallet_id_id, walked backwards. O(log n + page).
        var entries = db.LedgerEntries.AsNoTracking().Where(e => e.WalletId == walletId.Value);
        if (afterId is Guid cursor)
        {
            entries = entries.Where(e => e.Id < cursor);
        }

        List<LedgerEntryResponse> rows = await entries
            .OrderByDescending(e => e.Id)
            .Take(page.FetchSize)
            .Select(e => new LedgerEntryResponse(e.Id, e.AmountPoisha, e.Type.ToString(), e.RefId, e.CreatedAt))
            .ToListAsync(ct);

        return CursorPage<LedgerEntryResponse>.From(rows, page, r => r.Id);
    }

    internal static WalletResponse ToResponse(Wallet w) => new(w.Id, w.UserId, w.BalancePoisha, Currency);
}
