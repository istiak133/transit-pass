using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TransitPass.Network.Tests;

public sealed class StationEndpointTests(NetworkFactory factory) : IClassFixture<NetworkFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task List_returns_seeded_stations_sorted_by_id()
    {
        var stations = await _client.GetFromJsonAsync<JsonElement[]>(
            "/api/v1/network/stations", TestContext.Current.CancellationToken);

        // Seeded by the migration, returned in a stable order (ORDER BY id).
        stations!.Select(s => s.GetProperty("id").GetString())
            .ShouldBe(["agargaon", "karwan-bazar", "motijheel", "uttara-north"]);
    }

    [Fact]
    public async Task Get_by_id_returns_the_station()
    {
        var station = await _client.GetFromJsonAsync<JsonElement>(
            "/api/v1/network/stations/agargaon", TestContext.Current.CancellationToken);

        station.GetProperty("name").GetString().ShouldBe("Agargaon");
    }

    [Fact]
    public async Task Unknown_id_returns_404_problem_with_stable_code()
    {
        var response = await _client.GetAsync(
            "/api/v1/network/stations/dhanmondi", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("station.not_found");
        problem.TryGetProperty("traceId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Id_lookup_is_case_sensitive()
    {
        var response = await _client.GetAsync(
            "/api/v1/network/stations/Agargaon", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
