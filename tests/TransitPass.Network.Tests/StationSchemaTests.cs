using Microsoft.EntityFrameworkCore;
using Npgsql;
using TransitPass.Network.Api.Stations;

namespace TransitPass.Network.Tests;

/// <summary>The database itself must refuse bad stations, even if application code has a bug.</summary>
public sealed class StationSchemaTests(NetworkFactory factory) : IClassFixture<NetworkFactory>
{
    [Theory]
    [InlineData("Agargaon")]     // uppercase
    [InlineData("mirpur 10")]    // space
    [InlineData("-mirpur")]      // leading hyphen
    [InlineData("mirpur--10")]   // double hyphen
    public async Task Non_slug_id_is_rejected_by_check_constraint(string id)
    {
        await using var db = factory.CreateDbContext();
        db.Stations.Add(new Station { Id = id, Name = $"Test {Guid.NewGuid()}" });

        var ex = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var pg = ex.InnerException.ShouldBeOfType<PostgresException>();
        pg.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        pg.ConstraintName.ShouldBe("ck_stations_id_slug");
    }

    [Fact]
    public async Task Duplicate_name_is_rejected_by_unique_index()
    {
        await using var db = factory.CreateDbContext();
        db.Stations.Add(new Station { Id = "agargaon-two", Name = "Agargaon" });

        var ex = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var pg = ex.InnerException.ShouldBeOfType<PostgresException>();
        pg.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.ShouldBe("ux_stations_name");
    }

    [Fact]
    public async Task Lookup_by_id_can_use_the_primary_key_index()
    {
        await using var db = factory.CreateDbContext();
        var ct = TestContext.Current.CancellationToken;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 4 rows fit in one page, so the planner rightly prefers a Seq Scan. Forbid it to prove the index is usable.
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off", ct);
        var plan = await db.Database
            .SqlQueryRaw<string>("EXPLAIN SELECT id, name FROM stations WHERE id = 'agargaon'")
            .ToListAsync(ct);

        string.Join('\n', plan).ShouldContain("Index Scan using pk_stations");
    }
}
