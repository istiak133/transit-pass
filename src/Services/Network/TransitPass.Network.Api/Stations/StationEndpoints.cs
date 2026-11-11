using Microsoft.EntityFrameworkCore;
using TransitPass.BuildingBlocks.Http;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Network.Api.Data;

namespace TransitPass.Network.Api.Stations;

/// <summary>Station endpoints under /api/v1/network/stations.</summary>
public static class StationEndpoints
{
    /// <summary>Maps the station routes.</summary>
    public static IEndpointRouteBuilder MapStationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/network/stations");

        // GET /api/v1/network/stations → 200 with every station
        group.MapGet("/", async (NetworkDbContext db, CancellationToken ct) =>
            TypedResults.Ok(await ListAsync(db, ct)));

        // GET /api/v1/network/stations/{id} → 200 with one station, or 404 problem details
        group.MapGet("/{id}", async (string id, NetworkDbContext db, CancellationToken ct) =>
            (await FindAsync(db, id, ct)).ToHttp());

        return app;
    }

    // SQL: SELECT s.id, s.name FROM stations AS s ORDER BY s.id
    // AsNoTracking: read-only, so EF Core skips the change tracker (less memory and CPU).
    // Select into the response record: only the needed columns are fetched, no entity is materialized.
    internal static Task<List<StationResponse>> ListAsync(NetworkDbContext db, CancellationToken ct) =>
        db.Stations.AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => new StationResponse(s.Id, s.Name))
            .ToListAsync(ct);

    // SQL: SELECT s.id, s.name FROM stations AS s WHERE s.id = @id LIMIT 1
    // The WHERE on the primary key is answered by the pk_stations B+ tree index: O(log n).
    internal static async Task<Result<StationResponse>> FindAsync(NetworkDbContext db, string id, CancellationToken ct)
    {
        var station = await db.Stations.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new StationResponse(s.Id, s.Name))
            .FirstOrDefaultAsync(ct);

        return station is not null
            ? station
            : Error.NotFound("station.not_found", $"Station '{id}' does not exist.");
    }
}
