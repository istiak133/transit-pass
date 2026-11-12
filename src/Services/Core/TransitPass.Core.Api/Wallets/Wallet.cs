namespace TransitPass.Core.Api.Wallets;

/// <summary>
/// One wallet per user. <see cref="BalancePoisha"/> is a cache of SUM(ledger_entries.amount_poisha):
/// both change in the same transaction (see <see cref="Ledger.LedgerBook"/>), so they never disagree.
/// </summary>
public sealed class Wallet
{
    public required Guid Id { get; init; }

    /// <summary>The Identity user id, taken from the token's sub. No FK: users live in another database.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Money as whole poisha (৳1 = 100 poisha). Never float/double: 0.1 + 0.2 != 0.3 in binary.</summary>
    public long BalancePoisha { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>What the API returns for a wallet.</summary>
public sealed record WalletResponse(Guid WalletId, Guid UserId, long BalancePoisha, string Currency);
