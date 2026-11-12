using Microsoft.EntityFrameworkCore;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Core.Api.Data;

namespace TransitPass.Core.Api.Ledger;

/// <summary>
/// The only code that moves money. Every posting changes the balance AND appends a ledger row in one
/// database transaction, so SUM(ledger) == balance always holds (atomic: both happen or neither).
/// </summary>
public static class LedgerBook
{
    /// <summary>
    /// Adds <paramref name="amountPoisha"/> (negative to take money out) to a wallet.
    /// Joins the caller's transaction if one is open (Phase 8 and 9 post inside bigger transactions),
    /// otherwise opens its own. Returns the new balance, or wallet.insufficient_funds.
    /// </summary>
    public static async Task<Result<long>> PostAsync(
        CoreDbContext db, Guid walletId, long amountPoisha, LedgerEntryType type, Guid refId,
        DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentOutOfRangeException.ThrowIfZero(amountPoisha);

        bool ownTransaction = db.Database.CurrentTransaction is null;
        await using var tx = ownTransaction ? await db.Database.BeginTransactionAsync(ct) : null;

        // 1. Atomic conditional update. "Read balance, check in C#, write" would race: two debits could
        //    both read 100 and both succeed. Here Postgres locks the row, re-checks the WHERE on the
        //    latest version, and changes it in one step. The second debit waits, then sees the new balance.
        //    The WHERE turns "not enough money" into 0 rows (a clean error) instead of tripping the CHECK,
        //    whose error would abort the whole transaction.
        List<long> balance = await db.Database
            .SqlQuery<long>($"""
                UPDATE wallets
                   SET balance_poisha = balance_poisha + {amountPoisha}
                 WHERE id = {walletId}
                   AND balance_poisha + {amountPoisha} >= 0
                RETURNING balance_poisha AS "Value"
                """)
            .ToListAsync(ct);

        if (balance.Count == 0)
        {
            return Error.BusinessRule("wallet.insufficient_funds", "The wallet balance is too low.");
        }

        // 2. Append the history row in the same transaction.
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = Guid.CreateVersion7(now),
            WalletId = walletId,
            AmountPoisha = amountPoisha,
            Type = type,
            RefId = refId,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }

        return balance[0];
    }
}
