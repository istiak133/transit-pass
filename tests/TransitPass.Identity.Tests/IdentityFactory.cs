using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using TransitPass.Identity.Api.Data;

namespace TransitPass.Identity.Tests;

/// <summary>
/// Hosts the real Identity service in memory against a real, throwaway PostgreSQL in Docker
/// (same major version as docker-compose). The migration runs on startup, so tests see the real schema.
/// </summary>
public sealed class IdentityFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _ = Server; // start the host now, which applies the migration
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
    }

    /// <summary>A fresh DbContext for arranging or inspecting data directly.</summary>
    public IdentityDbContext CreateDbContext() =>
        Services.CreateScope().ServiceProvider.GetRequiredService<IdentityDbContext>();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
