using Microsoft.EntityFrameworkCore;
using TransitPass.Core.Api.Data;
using TransitPass.Core.Api.Ledger;

namespace TransitPass.Core.Api.Jobs;

/// <summary>What one run of the expiry job did.</summary>
public sealed record ExpiryRun(int Expired, long RefundedPoisha, int Failed);

/// <summary>
/// Expire every Active ticket whose service day is over, and give its fare back. The policy (PRD "used nothing →
/// Expired + refund"): a single-journey ticket that never entered a gate costs nothing; the full fare returns as a
/// Refund ledger row. Each batch is ONE transaction:
///   1. SELECT … FOR UPDATE OF t SKIP LOCKED: lock up to N due tickets that nobody else holds
///   2. per ticket: guarded UPDATE Active → Expired, then LedgerBook credits +fare (type Refund, ref = ticket id)
/// Many Core instances can run this at the same moment: SKIP LOCKED hands each one different rows, the guard and
/// ux_ledger_entries_refund_ref make a second refund impossible anyway.
/// </summary>
public static class TicketExpiry
{
    private const string Savepoint = "expire_ticket";

    /// <summary>One due ticket, with the wallet its refund goes to (null if, against all rules, there is none).</summary>
    internal sealed class DueTicket
    {
        public Guid Id { get; set; }

        public long FarePoisha { get; set; }

        public Guid? WalletId { get; set; }
    }

    /// <summary>
    /// Runs batches until nothing due is left (or only rows that failed in this run). A failed row stays Active
    /// and is tried again on the next run; it is skipped for the rest of this one, so it can't block the loop.
    /// </summary>
    public static async Task<ExpiryRun> RunAsync(
        CoreDbContext db, DateTimeOffset now, int batchSize, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var failed = new HashSet<Guid>();
        int expired = 0;
        long refunded = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            (int locked, int batchExpired, long batchRefunded) = await ExpireBatchAsync(db, now, batchSize, failed, logger, ct);
            expired += batchExpired;
            refunded += batchRefunded;
            if (locked < batchSize)
            {
                break; // a short batch: everything due (and not held by another instance) is done
            }
        }

        return new ExpiryRun(expired, refunded, failed.Count);
    }

    private static async Task<(int Locked, int Expired, long Refunded)> ExpireBatchAsync(
        CoreDbContext db, DateTimeOffset now, int batchSize, HashSet<Guid> failed, ILogger logger, CancellationToken ct)
    {
        db.ChangeTracker.Clear(); // nothing left over from an earlier batch may be saved again
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1. Lock a batch. The WHERE and ORDER BY match the partial index ix_tickets_active_expiry (only Active rows,
        //    sorted by expires_at), so this reads the oldest due rows straight off the index: O(log n + batch).
        //    SKIP LOCKED: rows another instance (or a cancel/gate in flight) holds are passed over, never waited for.
        //    OF t: lock the tickets only; the wallet join just tells us where each refund goes.
        // Rows that failed earlier in this run, as one text parameter split back into uuid[] by Postgres.
        // (Still a bound parameter, never SQL text; a string keeps the interpolation type-obvious for CodeQL.)
        string skip = string.Join(',', failed);
        List<DueTicket> due = await db.Database
            .SqlQuery<DueTicket>($"""
                SELECT t.id AS "Id", t.fare_poisha AS "FarePoisha", w.id AS "WalletId"
                  FROM tickets t
                  LEFT JOIN wallets w ON w.user_id = t.user_id
                 WHERE t.status = 'Active'
                   AND t.expires_at <= {now}
                   AND NOT (t.id = ANY(string_to_array({skip}, ',')::uuid[]))
                 ORDER BY t.expires_at
                 LIMIT {batchSize}
                   FOR UPDATE OF t SKIP LOCKED
                """)
            .ToListAsync(ct);

        int expired = 0;
        long refunded = 0;

        // 2. Refund in wallet order. Two instances whose batches share wallets W1 and W2 both lock W1 first, so they
        //    can never wait on each other in a circle (no deadlock). Tickets are already ours, so their order is free.
        foreach (DueTicket ticket in due.OrderBy(d => d.WalletId).ThenBy(d => d.Id))
        {
            if (ticket.WalletId is not Guid walletId)
            {
                // A ticket is paid from a wallet, so this "can't happen". If it does, don't guess: leave it Active.
                failed.Add(ticket.Id);
                logger.LogError("Ticket {TicketId} is due to expire but its owner has no wallet; it stays Active.", ticket.Id);
                continue;
            }

            // A savepoint per ticket: if this one throws, only its own changes roll back, not the whole batch.
            await tx.CreateSavepointAsync(Savepoint, ct);
            try
            {
                // The guard. We hold the row lock, so this matches 1 row; it is here so that a second refund is
                // impossible even if someone changes the query above one day.
                int changed = await db.Database.ExecuteSqlAsync(
                    $"UPDATE tickets SET status = 'Expired' WHERE id = {ticket.Id} AND status = 'Active'", ct);
                if (changed == 0)
                {
                    await tx.ReleaseSavepointAsync(Savepoint, ct);
                    continue;
                }

                var refund = await LedgerBook.PostAsync(
                    db, walletId, ticket.FarePoisha, LedgerEntryType.Refund, ticket.Id, now, ct);
                if (!refund.IsSuccess)
                {
                    // A credit can't fail the balance check, but never keep an Expired ticket without its refund.
                    throw new InvalidOperationException($"Refund for ticket {ticket.Id} failed: {refund.Error.Code}");
                }

                await tx.ReleaseSavepointAsync(Savepoint, ct);
                expired++;
                refunded += ticket.FarePoisha;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad row must not stop the others: undo just this ticket, remember it, carry on.
                await tx.RollbackToSavepointAsync(Savepoint, ct);
                db.ChangeTracker.Clear(); // the unsaved ledger row must not ride along with the next SaveChanges
                failed.Add(ticket.Id);
                logger.LogError(ex, "Ticket {TicketId} could not be expired; it stays Active and will be retried.", ticket.Id);
            }
        }

        await tx.CommitAsync(ct);
        return (due.Count, expired, refunded);
    }
}
