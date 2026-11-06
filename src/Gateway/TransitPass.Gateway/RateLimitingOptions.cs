namespace TransitPass.Gateway;

/// <summary>
/// Token-bucket settings for the per-client limit at the edge.
/// Each client has a bucket of <see cref="TokenLimit"/> tokens. A request takes one. Every
/// <see cref="ReplenishmentPeriod"/>, <see cref="TokensPerPeriod"/> tokens are added back, up to the limit.
/// This allows short bursts (a page that fires five calls) but caps the sustained rate.
/// </summary>
internal sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public int TokenLimit { get; set; } = 100;

    public int TokensPerPeriod { get; set; } = 50;

    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromSeconds(1);
}
