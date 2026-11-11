using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using TransitPass.Network.Api.Data;

namespace TransitPass.Network.Tests;

/// <summary>
/// Hosts the real Network service in memory against a real, throwaway PostgreSQL in Docker
/// (same major version as docker-compose). The migration runs on startup, so tests see the real schema.
/// </summary>
public sealed class NetworkFactory : WebApplicationFactory<Program>, IAsyncLifetime
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
    public NetworkDbContext CreateDbContext() =>
        Services.CreateScope().ServiceProvider.GetRequiredService<NetworkDbContext>();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
