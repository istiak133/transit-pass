namespace TransitPass.Core.Api.Jobs;

/// <summary>Settings under "Jobs" for the ticket expiry job.</summary>
public sealed class ExpiryOptions
{
    public const string Section = "Jobs";

    /// <summary>On in every environment by default. Tests turn it off so no job touches their tickets behind their back.</summary>
    public bool ExpiryEnabled { get; set; } = true;

    /// <summary>How long the job waits between runs. Each run keeps going until nothing is left to expire.</summary>
    public TimeSpan ExpiryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How many tickets one transaction locks and expires. Small enough to keep locks short.</summary>
    public int ExpiryBatchSize { get; set; } = 100;

    /// <summary>The shortest interval allowed. PeriodicTimer itself refuses anything under 1 ms.</summary>
    internal static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest interval allowed. PeriodicTimer refuses anything over about 49 days.</summary>
    internal static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// The rules a valid configuration follows; checked when Core starts, so a typo stops it at once. The interval
    /// range matters: a value PeriodicTimer refuses would otherwise pass startup and crash the worker a moment later.
    /// </summary>
    internal bool IsValid() =>
        ExpiryInterval >= MinInterval && ExpiryInterval <= MaxInterval && ExpiryBatchSize is >= 1 and <= 1000;
}
