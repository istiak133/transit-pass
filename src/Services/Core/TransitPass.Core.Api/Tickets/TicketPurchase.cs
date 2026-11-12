using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Core.Api.Data;
using TransitPass.Core.Api.Fares;
using TransitPass.Core.Api.Idempotency;
using TransitPass.Core.Api.Ledger;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Api.Tickets;

/// <summary>The JSON to send back, the ticket it describes, and whether it is a replay of an earlier answer.</summary>
public sealed record PurchaseOutcome(string ResponseJson, Guid TicketId, bool Replayed);

/// <summary>Buying a ticket: the money and the ticket change together, exactly once per Idempotency-Key.</summary>
public static partial class TicketPurchase
{
    internal const int MaxKeyLength = 64;

    [GeneratedRegex("^[A-Za-z0-9_:.-]{1,64}$")]
    private static partial Regex KeyShape();

    /// <summary>
    /// 1. validate → 2. a key we've seen? replay → 3. ask Network for the fare (HTTP, OUTSIDE any transaction:
    /// a slow network call must never hold row locks) → 4. one transaction: claim the key, debit, insert the ticket,
    /// store the answer → commit.
    /// </summary>
    internal static async Task<Result<PurchaseOutcome>> PurchaseAsync(
        Guid userId, string? idempotencyKey, BuyTicketRequest body, CoreDbContext db, IFareQuotes fares,
        TimeProvider clock, CancellationToken ct)
    {
        // 1. Validate. Cheap checks first; nothing below runs for a bad request.
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return Error.Validation("idempotency.key_required", "The Idempotency-Key header is required.");
        }

        if (!KeyShape().IsMatch(idempotencyKey))
        {
            return Error.Validation("idempotency.invalid_key",
                "Idempotency-Key must be 1–64 characters: letters, digits, '_', '-', ':' or '.'.");
        }

        string from = body.From?.Trim() ?? string.Empty;
        string to = body.To?.Trim() ?? string.Empty;
        if (from.Length is 0 or > 64 || to.Length is 0 or > 64)
        {
            return Error.Validation("ticket.invalid_request", "Both 'from' and 'to' station ids are required.");
        }

        string requestHash = Hash(from, to);

        // 2. Seen this key? Then this is a retry: answer as before, without calling Network or touching money.
        IdempotencyRecord? seen = await FindAsync(db, userId, idempotencyKey, ct);
        if (seen is not null)
        {
            return Replay(seen, requestHash);
        }

        // 3. The fare, from the service that owns the track graph. No transaction is open yet.
        var quote = await fares.QuoteAsync(from, to, ct);
        if (!quote.IsSuccess)
        {
            return quote.Error;
        }

        DateTimeOffset now = clock.GetUtcNow();
        Wallet wallet = await WalletEndpoints.GetOrCreateAsync(db, userId, now, ct);

        // 4. One transaction: all of it commits, or none of it does.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 4a. Claim the key. If a twin request holds it uncommitted, Postgres makes us WAIT here until it
        //     commits (then we insert nothing and replay its answer) or rolls back (then we go on).
        int claimed = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO idempotency_keys (user_id, key, request_hash, created_at)
            VALUES ({userId}, {idempotencyKey}, {requestHash}, {now})
            ON CONFLICT (user_id, key) DO NOTHING
            """, ct);
        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            seen = await FindAsync(db, userId, idempotencyKey, ct);
            return seen is null
                ? Error.Conflict("idempotency.in_progress", "A request with this key is still being processed.")
                : Replay(seen, requestHash);
        }

        // 4b. Take the money: Phase 7's atomic conditional UPDATE. Not enough → error → everything rolls back,
        //     the key too, so the same key can be used again after a top-up.
        var ticket = new Ticket
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            FromStation = quote.Value.From,
            ToStation = quote.Value.To,
            FarePoisha = quote.Value.FarePoisha,
            DistanceM = quote.Value.DistanceM,
            Status = TicketStatus.Active,
            PurchasedAt = now,
            ExpiresAt = Ticket.ExpiryFor(now),
        };
        var debit = await LedgerBook.PostAsync(db, wallet.Id, -ticket.FarePoisha, LedgerEntryType.Purchase, ticket.Id, now, ct);
        if (!debit.IsSuccess)
        {
            return debit.Error;
        }

        // 4c. The ticket, and 4d. the answer stored under the key.
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);
        string json = JsonSerializer.Serialize(TicketEndpoints.ToResponse(ticket), JsonSerializerOptions.Web);
        await db.IdempotencyKeys
            .Where(r => r.UserId == userId && r.Key == idempotencyKey)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TicketId, ticket.Id).SetProperty(r => r.ResponseBody, json), ct);

        await tx.CommitAsync(ct);
        return new PurchaseOutcome(json, ticket.Id, Replayed: false);
    }

    private static Task<IdempotencyRecord?> FindAsync(CoreDbContext db, Guid userId, string key, CancellationToken ct) =>
        db.IdempotencyKeys.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == userId && r.Key == key, ct);

    private static Result<PurchaseOutcome> Replay(IdempotencyRecord seen, string requestHash)
    {
        if (seen.RequestHash != requestHash)
        {
            // Same key, different trip: almost certainly a client bug. Refuse rather than guess.
            return Error.BusinessRule("idempotency.key_reused", "This Idempotency-Key was already used for a different request.");
        }

        return seen is { ResponseBody: string json, TicketId: Guid id }
            ? new PurchaseOutcome(json, id, Replayed: true)
            : Error.Conflict("idempotency.in_progress", "A request with this key is still being processed.");
    }

    internal static string Hash(string from, string to) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{from}\n{to}")));
}
