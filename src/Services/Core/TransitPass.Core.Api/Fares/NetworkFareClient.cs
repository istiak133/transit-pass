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

/// <summary>
/// HTTP client for Network's <c>GET /api/v1/network/fares/quote</c>. Network's own answers (unknown station,
/// same station, no route) pass through with their codes. Anything else (down, 5xx, timeout, garbage)
/// becomes <c>503 network.unavailable</c>, so a Network problem never looks like the user's mistake.
/// </summary>
internal sealed class NetworkFareClient(HttpClient http, ILogger<NetworkFareClient> logger) : IFareQuotes
{
    internal const string NetworkUrlKey = "Services:NetworkUrl";
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<Result<FareQuote>> QuoteAsync(string from, string to, CancellationToken ct)
    {
        string url = $"api/v1/network/fares/quote?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}";
        try
        {
            using HttpResponseMessage response = await http.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
            {
                FareQuote? quote = await response.Content.ReadFromJsonAsync<FareQuote>(ct);
                return quote is { FarePoisha: > 0 } ? quote : Unavailable("Network returned an unusable fare.");
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
        logger.LogWarning("Fare quote failed: {Reason}", why);
        return Error.Unavailable("network.unavailable", "Fares are unavailable right now. Please try again.");
    }
}
