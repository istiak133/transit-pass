using System.Globalization;
using System.Threading.RateLimiting;

namespace TransitPass.Identity.Api.Sessions;

/// <summary>
/// At most <see cref="AttemptsPerWindow"/> login attempts per phone number per minute, whatever IP they come from.
/// The gateway already limits per IP; this stops a password-guessing attack spread across many IPs at one account.
/// </summary>
public sealed class LoginThrottle : IDisposable
{
    /// <summary>Attempts allowed per phone per window.</summary>
    public const int AttemptsPerWindow = 5;

    private readonly PartitionedRateLimiter<string> _limiter =
        PartitionedRateLimiter.Create<string, string>(phone =>
            RateLimitPartition.GetFixedWindowLimiter(phone, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = AttemptsPerWindow,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0, // never queue a login: answer 429 at once
            }));

    /// <summary>True if this attempt may go ahead; otherwise how many seconds until the next one may.</summary>
    public bool TryAcquire(string phone, out string? retryAfterSeconds)
    {
        using RateLimitLease lease = _limiter.AttemptAcquire(phone);
        retryAfterSeconds = null;
        if (lease.IsAcquired)
        {
            return true;
        }

        retryAfterSeconds = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan wait)
            ? Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture)
            : "60";
        return false;
    }

    /// <inheritdoc />
    public void Dispose() => _limiter.Dispose();
}
