using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TransitPass.Network.Tests;

public sealed class FareEndpointTests(NetworkFactory factory) : IClassFixture<NetworkFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData("uttara-north", "motijheel", 16_500, 10_000)] // three segments: 9000 + 4000 + 3500
    [InlineData("motijheel", "uttara-north", 16_500, 10_000)] // reverse direction, same fare
    [InlineData("agargaon", "karwan-bazar", 4_000, 3_000)]
    [InlineData("uttara-north", "agargaon", 9_000, 6_000)]
    public async Task Quotes_distance_and_fare(string from, string to, long metres, long poisha)
    {
        var quote = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/network/fares/quote?from={from}&to={to}", TestContext.Current.CancellationToken);

        quote.GetProperty("distanceM").GetInt64().ShouldBe(metres);
        quote.GetProperty("farePoisha").GetInt64().ShouldBe(poisha);
    }

    [Theory]
    [InlineData("?from=agargaon", HttpStatusCode.BadRequest, "fare.invalid_request")]
    [InlineData("?to=agargaon", HttpStatusCode.BadRequest, "fare.invalid_request")]
    [InlineData("", HttpStatusCode.BadRequest, "fare.invalid_request")]
    [InlineData("?from=agargaon&to=agargaon", HttpStatusCode.BadRequest, "fare.same_station")]
    [InlineData("?from=agargaon&to=dhanmondi", HttpStatusCode.NotFound, "station.not_found")]
    public async Task Invalid_requests_return_problem_with_code(string query, HttpStatusCode status, string code)
    {
        var response = await _client.GetAsync($"/api/v1/network/fares/quote{query}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(status);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe(code);
    }
}
