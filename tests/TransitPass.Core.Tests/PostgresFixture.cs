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
    // Every factory has its own pool (up to 100 connections), and test classes run in parallel; the
    // 200-purchase race alone can fill one pool. Postgres's default of 100 connections is too few for that.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=400")
        .Build();

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
