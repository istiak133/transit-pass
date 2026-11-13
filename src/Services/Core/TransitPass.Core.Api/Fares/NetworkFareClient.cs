using System.Net.Http.Json;
using System.Text.Json;
using TransitPass.BuildingBlocks.Results;

namespace TransitPass.Core.Api.Fares;

/// <summary>A fare as the Network service quoted it (Phase 3's response shape).</summary>
public sealed record FareQuote(string From, string To, long DistanceM, long FarePoisha);

/// <summary>Asks the Network service what a trip costs. Core never computes fares itself: Network owns the graph.</summary>
public interface IFareQuotes
{
    Task<Result<FareQuote>> QuoteAsync(string from, string to, CancellationToken ct);
}

/// <summary>Asks the Network service whether a station exists. The gate checks its own station before an entry.</summary>
public interface IStationDirectory
{
    /// <summary>Success if the station exists; <c>404 station.not_found</c>, or <c>503 network.unavailable</c>.</summary>
    Task<Result> EnsureExistsAsync(string stationId, CancellationToken ct);
}

/// <summary>
/// HTTP client for Network's <c>GET /api/v1/network/fares/quote</c> and <c>GET /api/v1/network/stations/{id}</c>. Network's own answers (unknown station,
/// same station, no route) pass through with their codes. Anything else (down, 5xx, timeout, garbage)
/// becomes <c>503 network.unavailable</c>, so a Network problem never looks like the user's mistake.
/// </summary>
internal sealed class NetworkFareClient(HttpClient http, ILogger<NetworkFareClient> logger) : IFareQuotes, IStationDirectory
{
    internal const string NetworkUrlKey = "Services:NetworkUrl";
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public Task<Result<FareQuote>> QuoteAsync(string from, string to, CancellationToken ct) =>
        GetAsync<FareQuote>(
            $"api/v1/network/fares/quote?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}",
            quote => quote is { FarePoisha: > 0 }, "Network returned an unusable fare.", ct);

    public async Task<Result> EnsureExistsAsync(string stationId, CancellationToken ct)
    {
        var station = await GetAsync<StationInfo>(
            $"api/v1/network/stations/{Uri.EscapeDataString(stationId)}",
            s => s is { Id.Length: > 0 }, "Network returned an unusable station.", ct);
        return station.IsSuccess ? Result.Success() : station.Error;
    }

    /// <summary>The station fields Core needs (Network's response has more).</summary>
    private sealed record StationInfo(string Id);

    /// <summary>
    /// One GET with the shared mapping: 2xx and <paramref name="usable"/> → the value; Network's own 400/404/422
    /// pass through with their codes; anything else → 503.
    /// </summary>
    private async Task<Result<T>> GetAsync<T>(string url, Func<T?, bool> usable, string unusable, CancellationToken ct)
        where T : class
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(new Uri(url, UriKind.Relative), ct);
            if (response.IsSuccessStatusCode)
            {
                T? value = await response.Content.ReadFromJsonAsync<T>(ct);
                return usable(value) ? value! : Unavailable(unusable);
            }

            ErrorType? passThrough = (int)response.StatusCode switch
            {
                400 => ErrorType.Validation,
                404 => ErrorType.NotFound,
                422 => ErrorType.BusinessRule,
                _ => null,
            };
            if (passThrough is ErrorType type)
            {
                JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                return new Error(problem.GetProperty("code").GetString()!, problem.GetProperty("title").GetString()!, type);
            }

            return Unavailable($"Network answered {(int)response.StatusCode}.");
        }
        catch (HttpRequestException ex)
        {
            return Unavailable(ex.Message); // refused, DNS, reset…
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unavailable("Network did not answer in time."); // our 3 s timeout, not the caller giving up
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Unavailable("Network sent a malformed answer.");
        }
    }

    private Error Unavailable(string why)
    {
        logger.LogWarning("Network call failed: {Reason}", why);
        return Error.Unavailable("network.unavailable", "Fares are unavailable right now. Please try again.");
    }
}
