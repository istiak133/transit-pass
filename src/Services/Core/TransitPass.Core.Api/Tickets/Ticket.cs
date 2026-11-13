namespace TransitPass.Core.Api.Tickets;

/// <summary>
/// A ticket's life. Arrows only go forward: Active → InJourney → Used (gates, Phase 12),
/// Active → Cancelled (Phase 10), Active → Expired (Phase 13).
/// </summary>
public enum TicketStatus
{
    Active,
    InJourney,
    Used,
    Cancelled,
    Expired,
}

/// <summary>One single-journey ticket. Stations and fare are copied at purchase time (a snapshot).</summary>
public sealed class Ticket
{
    public required Guid Id { get; init; }

    /// <summary>Owner, from the token. No FK: users live in identity_db.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Snapshot of the station id. Network may rename later; the ticket keeps what was bought.</summary>
    public required string FromStation { get; init; }

    public required string ToStation { get; init; }

    /// <summary>Snapshot of the price paid. Refunds use this, never today's price.</summary>
    public required long FarePoisha { get; init; }

    public required long DistanceM { get; init; }

    public TicketStatus Status { get; set; }

    public required DateTimeOffset PurchasedAt { get; init; }

    /// <summary>Midnight (Dhaka time) after the purchase: a single-journey ticket is good for that service day.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>When Rafi cancelled it; null for every other status (a CHECK enforces the pairing).</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>Dhaka is UTC+6 all year (no daylight saving), so a fixed offset is exact.</summary>
    internal static readonly TimeSpan Dhaka = TimeSpan.FromHours(6);

    /// <summary>
    /// The next Dhaka midnight after <paramref name="now"/>, as UTC: Npgsql stores timestamptz only from
    /// UTC values (an offset of +06:00 would be refused), and the database keeps instants, not offsets.
    /// </summary>
    internal static DateTimeOffset ExpiryFor(DateTimeOffset now)
    {
        DateTimeOffset local = now.ToOffset(Dhaka);
        return new DateTimeOffset(local.Date.AddDays(1), Dhaka).ToUniversalTime();
    }
}

/// <summary>Buy a ticket. Nullable fields so a missing one is a clear 400.</summary>
public sealed record BuyTicketRequest(string? From, string? To);

/// <summary>A ticket as the API shows it.</summary>
public sealed record TicketResponse(
    Guid Id, string From, string To, long FarePoisha, long DistanceM, string Status,
    DateTimeOffset PurchasedAt, DateTimeOffset ExpiresAt, DateTimeOffset? CancelledAt = null);
