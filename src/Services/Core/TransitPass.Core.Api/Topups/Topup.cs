namespace TransitPass.Core.Api.Topups;

/// <summary>
/// A top-up's life. Arrows only go forward, and only a verified payment webhook moves them:
/// <c>Pending → Succeeded</c> (money credited) or <c>Pending → Failed</c> (nothing credited).
/// </summary>
public enum TopupStatus
{
    Pending,
    Succeeded,
    Failed,
}

/// <summary>"Rafi wants to add ৳500." Created by the app, completed by the payment provider.</summary>
public sealed class Topup
{
    public required Guid Id { get; init; }

    public required Guid WalletId { get; init; }

    public required long AmountPoisha { get; init; }

    public TopupStatus Status { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it left Pending. Null exactly while Pending (a CHECK enforces it).</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Start a top-up. Nullable so a missing amount is a clear 400, not a parse error.</summary>
public sealed record CreateTopupRequest(long? AmountPoisha);

/// <summary>A top-up as the API shows it. <c>CheckoutUrl</c> is where the client sends the user to pay.</summary>
public sealed record TopupResponse(
    Guid Id, long AmountPoisha, string Status, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string CheckoutUrl);
