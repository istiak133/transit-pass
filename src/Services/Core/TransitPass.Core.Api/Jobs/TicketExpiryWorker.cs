using Microsoft.Extensions.Options;
using TransitPass.Core.Api.Data;

namespace TransitPass.Core.Api.Jobs;

/// <summary>
/// Runs <see cref="TicketExpiry"/> once at startup and then every <see cref="ExpiryOptions.ExpiryInterval"/>.
/// Every Core instance runs it; SKIP LOCKED lets them share the work without a leader or a distributed lock.
/// </summary>
public sealed partial class TicketExpiryWorker(
    IServiceScopeFactory scopes, IOptions<ExpiryOptions> options, TimeProvider clock, ILogger<TicketExpiryWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ExpiryOptions settings = options.Value;
        if (!settings.ExpiryEnabled)
        {
            LogDisabled(logger);
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(settings.ExpiryInterval, clock);
            do
            {
                await RunOnceAsync(settings.ExpiryBatchSize, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. The open batch transaction (if any) was rolled back: its tickets stay Active, nothing is lost.
            LogStopped(logger);
        }
    }

    /// <summary>One run. Any failure (the database is down, say) is logged and the next tick tries again.</summary>
    internal async Task RunOnceAsync(int batchSize, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            ExpiryRun run = await TicketExpiry.RunAsync(db, clock.GetUtcNow(), batchSize, logger, ct);
            if (run.Expired > 0 || run.Failed > 0)
            {
                LogRun(logger, run.Expired, run.RefundedPoisha, run.Failed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRunFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ticket expiry: {Expired} expired, {RefundedPoisha} poisha refunded, {Failed} failed.")]
    private static partial void LogRun(ILogger logger, int expired, long refundedPoisha, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ticket expiry run failed; the next run will try again.")]
    private static partial void LogRunFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ticket expiry job is off (Jobs:ExpiryEnabled = false).")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ticket expiry job stopped.")]
    private static partial void LogStopped(ILogger logger);
}
