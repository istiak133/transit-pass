using Microsoft.EntityFrameworkCore;
using Npgsql;
using TransitPass.Network.Api.Segments;

namespace TransitPass.Network.Tests;

/// <summary>The database refuses broken track data on its own.</summary>
public sealed class SegmentSchemaTests(NetworkFactory factory) : IClassFixture<NetworkFactory>
{
    [Theory]
    [InlineData("motijheel", "karwan-bazar", 3500, "ck_segments_canonical_order")] // reversed duplicate of a seeded row
    [InlineData("agargaon", "agargaon", 100, "ck_segments_canonical_order")]       // self-loop
    [InlineData("agargaon", "motijheel", 0, "ck_segments_distance_positive")]
    [InlineData("agargaon", "motijheel", -10, "ck_segments_distance_positive")]
    public async Task Bad_segment_is_rejected_by_check(string a, string b, int metres, string constraint)
    {
        var pg = await SaveAndGetErrorAsync(new Segment { StationA = a, StationB = b, DistanceM = metres });
        pg.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        pg.ConstraintName.ShouldBe(constraint);
    }

    [Fact]
    public async Task Duplicate_segment_is_rejected_by_primary_key()
    {
        var pg = await SaveAndGetErrorAsync(new Segment { StationA = "agargaon", StationB = "karwan-bazar", DistanceM = 1 });
        pg.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.ShouldBe("pk_segments");
    }

    [Fact]
    public async Task Segment_to_unknown_station_is_rejected_by_foreign_key()
    {
        var pg = await SaveAndGetErrorAsync(new Segment { StationA = "agargaon", StationB = "zzz-nowhere", DistanceM = 10 });
        pg.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        pg.ConstraintName.ShouldBe("fk_segments_station_b");
    }

    [Fact]
    public async Task Station_with_track_cannot_be_deleted()
    {
        await using var db = factory.CreateDbContext();
        var ct = TestContext.Current.CancellationToken;

        var ex = await Should.ThrowAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM stations WHERE id = 'agargaon'", ct));
        ex.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    private async Task<PostgresException> SaveAndGetErrorAsync(Segment segment)
    {
        await using var db = factory.CreateDbContext();
        db.Segments.Add(segment);
        var ex = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        return ex.InnerException.ShouldBeOfType<PostgresException>();
    }
}
