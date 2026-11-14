using System.Globalization;
using System.Threading.RateLimiting;

namespace TransitPass.Identity.Api.Sessions;

/// <summary>
/// At most <c>permitsPerMinute</c> attempts per key per minute (fixed window), whatever IP they come from.
/// The gateway already limits per IP; this limits per target (one account, one token) across many IPs.
/// The limiter keeps one small counter per active key in a dictionary: O(1) per check.
/// </summary>
public abstract class AttemptThrottle(int permitsPerMinute) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter =
        PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0, // never queue: answer 429 at once
            }));

    /// <summary>True if this attempt may go ahead; otherwise how many seconds until the next one may.</summary>
    public bool TryAcquire(string key, out string? retryAfterSeconds)
    {
        using RateLimitLease lease = _limiter.AttemptAcquire(key);
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
    public void Dispose()
    {
        _limiter.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// At most <see cref="AttemptsPerWindow"/> login attempts per phone number per minute: stops a password-guessing
/// attack spread across many IPs at one account.
/// </summary>
public sealed class LoginThrottle() : AttemptThrottle(AttemptsPerWindow)
{
    /// <summary>Attempts allowed per phone per window.</summary>
    public const int AttemptsPerWindow = 5;
}

/// <summary>
/// At most <see cref="AttemptsPerWindow"/> refresh or logout calls per refresh token per minute. A real client
/// uses a token once; a loop hammering one token (a buggy client, or a thief retrying) gets 429 before it
/// reaches the database and the family lock.
/// </summary>
public sealed class RefreshThrottle() : AttemptThrottle(AttemptsPerWindow)
{
    /// <summary>Attempts allowed per token per window.</summary>
    public const int AttemptsPerWindow = 10;
}
