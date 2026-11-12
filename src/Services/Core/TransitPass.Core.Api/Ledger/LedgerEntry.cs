namespace TransitPass.Core.Api.Ledger;

/// <summary>Why money moved. Stored as text; a CHECK constraint allows only these names.</summary>
public enum LedgerEntryType
{
    TopUp,
    Purchase,
    Refund,
    Surcharge,
}

/// <summary>
/// One movement of money. Rows are append-only: never updated, never deleted. A refund is a new
/// positive row, not an edit, so the history can always be replayed to prove the balance.
/// </summary>
public sealed class LedgerEntry
{
    /// <summary>UUIDv7: time-ordered, so ORDER BY id is ORDER BY time, and it doubles as the page cursor.</summary>
    public required Guid Id { get; init; }

    public required Guid WalletId { get; init; }

    /// <summary>Positive = money in, negative = money out. Never zero.</summary>
    public required long AmountPoisha { get; init; }

    public required LedgerEntryType Type { get; init; }

    /// <summary>What caused it: the top-up id or the ticket id.</summary>
    public required Guid RefId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>One row of the transaction history.</summary>
public sealed record LedgerEntryResponse(Guid Id, long AmountPoisha, string Type, Guid RefId, DateTimeOffset CreatedAt);
