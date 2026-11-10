using TransitPass.BuildingBlocks.Http;
using TransitPass.BuildingBlocks.Results;

namespace TransitPass.Network.Api.Stations;

/// <summary>Station endpoints under /api/v1/network/stations.</summary>
public static class StationEndpoints
{
    // In memory for now. Phase 2 moves this into PostgreSQL.
    private static readonly Station[] All =
    [
        new("uttara-north", "Uttara North"),
        new("agargaon", "Agargaon"),
        new("karwan-bazar", "Karwan Bazar"),
        new("motijheel", "Motijheel"),
    ];

    // Built once at startup: id → station. Lookup is O(1) instead of scanning the array, O(n).
    private static readonly Dictionary<string, Station> ById = All.ToDictionary(s => s.Id);

    /// <summary>Maps the station routes.</summary>
    public static IEndpointRouteBuilder MapStationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/network/stations");

        // GET /api/v1/network/stations → 200 with every station
        group.MapGet("/", () => TypedResults.Ok(All));

        // GET /api/v1/network/stations/{id} → 200 with one station, or 404 problem details
        group.MapGet("/{id}", (string id) => Find(id).ToHttp());

        return app;
    }

    internal static Result<Station> Find(string id) =>
        ById.TryGetValue(id, out var station)
            ? station
            : Error.NotFound("station.not_found", $"Station '{id}' does not exist.");
}
