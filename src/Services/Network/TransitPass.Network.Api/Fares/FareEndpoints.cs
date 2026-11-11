using TransitPass.BuildingBlocks.Http;
using TransitPass.BuildingBlocks.Results;

namespace TransitPass.Network.Api.Fares;

/// <summary>The fare for a trip between two stations.</summary>
public sealed record FareQuoteResponse(string From, string To, long DistanceM, long FarePoisha);

/// <summary>Fare endpoints under /api/v1/network/fares.</summary>
public static class FareEndpoints
{
    /// <summary>Maps the fare routes.</summary>
    public static IEndpointRouteBuilder MapFareEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/v1/network/fares/quote?from=uttara-north&to=motijheel
        // Query parameters, not path: this asks a question about two resources rather than naming one resource.
        app.MapGet("/api/v1/network/fares/quote",
            async (string? from, string? to, FareCatalog catalog, IFarePolicy policy, CancellationToken ct) =>
                Quote(await catalog.GetGraphAsync(ct), policy, from, to).ToHttp());

        return app;
    }

    internal static Result<FareQuoteResponse> Quote(RouteGraph graph, IFarePolicy policy, string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return Error.Validation("fare.invalid_request", "Both 'from' and 'to' are required.");
        }

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return Error.Validation("fare.same_station", "'from' and 'to' must be different stations.");
        }

        string? unknown = !graph.Contains(from) ? from : !graph.Contains(to) ? to : null;
        if (unknown is not null)
        {
            return Error.NotFound("station.not_found", $"Station '{unknown}' does not exist.");
        }

        if (!graph.TryGetDistance(from, to, out long metres))
        {
            return Error.BusinessRule("fare.no_route", $"There is no track between '{from}' and '{to}'.");
        }

        return new FareQuoteResponse(from, to, metres, policy.FareFor(metres));
    }
}
