using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TransitPass.Core.Api.Data;

[assembly: AssemblyFixture(typeof(TransitPass.Core.Tests.PostgresFixture))]

namespace TransitPass.Core.Tests;

/// <summary>
/// One real PostgreSQL (same major as docker-compose) for the whole test run, migrated once.
/// Tests stay independent because each one works with its own random user id.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    /// <summary>A DbContext straight on the test database, for arranging and inspecting rows.</summary>
    public CoreDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<CoreDbContext>().UseNpgsql(ConnectionString).Options);

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();
}
