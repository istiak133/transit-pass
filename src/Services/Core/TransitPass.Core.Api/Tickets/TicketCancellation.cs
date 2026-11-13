using Microsoft.EntityFrameworkCore;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Core.Api.Data;
using TransitPass.Core.Api.Ledger;

namespace TransitPass.Core.Api.Tickets;

/// <summary>
/// Cancel an unused ticket and refund its fare. One transaction:
///   1. UPDATE tickets SET status = 'Cancelled' WHERE id AND owner AND status = 'Active' AND not expired
///   2. credit the wallet +fare through the ledger (type Refund, ref = ticket id)
/// Step 1 is a guarded state change (compare-and-set): of a cancel and a gate scan arriving together,
/// Postgres lets exactly one of them move the ticket out of Active. The loser sees 0 rows.
/// </summary>
internal static class TicketCancellation
{
    public static async Task<Result<Ticket>> CancelAsync(
        Guid userId, Guid ticketId, CoreDbContext db, TimeProvider clock, CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1. The guard and the change in one statement. The row lock is held until COMMIT, so a gate
        //    (Phase 12) doing "WHERE status = 'Active'" at the same moment waits, then re-checks and finds
        //    'Cancelled'. The fare comes back from the row itself: refund what was paid, not today's price.
        List<long> fare = await db.Database
            .SqlQuery<long>($"""
                UPDATE tickets
                   SET status = 'Cancelled', cancelled_at = {now}
                 WHERE id = {ticketId}
                   AND user_id = {userId}
                   AND status = 'Active'
                   AND expires_at > {now}
                RETURNING fare_poisha AS "Value"
                """)
            .ToListAsync(ct);

        if (fare.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return await WhyNotAsync(userId, ticketId, db, now, ct);
        }

        // 2. The refund. The wallet must exist: the ticket was paid from it.
        Guid walletId = await db.Wallets.Where(w => w.UserId == userId).Select(w => w.Id).SingleAsync(ct);
        var refund = await LedgerBook.PostAsync(db, walletId, fare[0], LedgerEntryType.Refund, ticketId, now, ct);
        if (!refund.IsSuccess)
        {
            return refund.Error; // a credit cannot fail the balance check, but never commit half a cancel
        }

        await tx.CommitAsync(ct);
        return await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId, ct);
    }

    /// <summary>The UPDATE matched nothing. Read the ticket once to tell the caller why.</summary>
    private static async Task<Result<Ticket>> WhyNotAsync(
        Guid userId, Guid ticketId, CoreDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        Ticket? ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == ticketId && t.UserId == userId, ct);
        return ticket switch
        {
            // Not there, or someone else's: the same 404 (never confirm another user's ticket id).
            null => Error.NotFound("ticket.not_found", "No ticket with this id."),

            // Already cancelled (a retry after a lost response): same answer again, no second refund.
            { Status: TicketStatus.Cancelled } => ticket,

            { Status: TicketStatus.Active } => Error.Conflict(
                "ticket.expired", "This ticket's service day is over; it can no longer be cancelled."),

            _ => Error.Conflict(
                "ticket.not_cancellable", $"A ticket that is {ticket.Status} can no longer be cancelled."),
        };
    }
}
