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

    /// <summary>The rules a valid configuration follows; checked when Core starts, so a typo stops it at once.</summary>
    internal bool IsValid() =>
        ExpiryInterval > TimeSpan.Zero && ExpiryBatchSize is >= 1 and <= 1000;
}
