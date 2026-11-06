using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TransitPass.BuildingBlocks.Hosting;
using TransitPass.Gateway;

// The API gateway is the single public entry point. Clients only ever know this address.
// It does what is the same for every service (request id, rate limiting, later JWT checks),
// then YARP forwards the request to the service that owns the path.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Options pattern: the section is bound when first used, not while Program runs,
// so environment variables and test overrides are always picked up.
builder.Services.Configure<RateLimitingOptions>(builder.Configuration.GetSection(RateLimitingOptions.SectionName));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // One token bucket per client IP. The limiter keeps the buckets in a dictionary keyed by IP,
    // so each check is O(1) time, and memory is O(active clients). Idle buckets are cleaned up by the limiter.
    options.AddPolicy("per-client", context =>
    {
        var limits = context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        return RateLimitPartition.GetTokenBucketLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = limits.TokenLimit,
            TokensPerPeriod = limits.TokensPerPeriod,
            ReplenishmentPeriod = limits.ReplenishmentPeriod,
            QueueLimit = 0,            // reject at once rather than hold requests in memory
            AutoReplenishment = true,
        });
    });

    options.OnRejected = async (context, cancellationToken) =>
    {
        int retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
            ? (int)Math.Ceiling(wait.TotalSeconds)
            : 1;
        context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        var problems = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests. Retry later.",
                Type = "https://transitpass.dev/errors/gateway.rate_limited",
                Extensions = { ["code"] = "gateway.rate_limited" },
            },
        });
    };
});

var app = builder.Build();

app.UseServiceDefaults();
app.UseRateLimiter();

// Route table lives in appsettings.json ("ReverseProxy"). Docker Compose overrides the addresses.
app.MapReverseProxy().RequireRateLimiting("per-client");

await app.RunAsync();

/// <summary>Entry point marker so integration tests can host the gateway via WebApplicationFactory.</summary>
public partial class Program;
