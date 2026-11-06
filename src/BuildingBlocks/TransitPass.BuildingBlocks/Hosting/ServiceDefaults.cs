using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace TransitPass.BuildingBlocks.Hosting;

/// <summary>
/// What every TransitPass process (gateway and services) sets up the same way:
/// problem-details errors, a single clock, health endpoints, and a request id.
/// </summary>
public static class ServiceDefaults
{
    private const string ReadyTag = "ready";

    /// <summary>Registers the shared services.</summary>
    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Every error, including 404s and unhandled exceptions, becomes application/problem+json with a traceId.
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ctx =>
            ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier);

        // One clock for the whole app. Tests swap it for a fake one, so time-based rules are testable.
        builder.Services.AddSingleton(TimeProvider.System);

        builder.Services.AddHealthChecks();
        return builder;
    }

    /// <summary>
    /// Registers one <see cref="NpgsqlDataSource"/> for the connection string <paramref name="name"/>.
    /// The data source owns the connection pool: opening a "connection" borrows an already-open socket from the pool,
    /// and disposing it returns the socket. A readiness check on the database is added too.
    /// </summary>
    public static WebApplicationBuilder AddPostgres(this WebApplicationBuilder builder, string name = "Default")
    {
        ArgumentNullException.ThrowIfNull(builder);

        string connectionString = builder.Configuration.GetConnectionString(name)
            ?? throw new InvalidOperationException($"Connection string '{name}' is missing.");

        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        builder.Services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: [ReadyTag]);
        return builder;
    }

    /// <summary>Adds the shared middleware and the health endpoints.</summary>
    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();   // unhandled exception → generic 500 problem, no stack trace leaks
        app.UseStatusCodePages();    // bare 404/405 → problem details too

        // Liveness: "is the process up?" No dependency checks, so a slow database never gets the process killed.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

        // Readiness: "can it serve traffic?" Runs the checks tagged "ready", such as PostgreSQL.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        });

        return app;
    }
}
