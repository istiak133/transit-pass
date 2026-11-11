using TransitPass.Network.Api.Fares;

namespace TransitPass.Network.Tests;

public sealed class RouteGraphTests
{
    //  A ──9── B ──4── C ──3── D        E (isolated)
    //  └──────────20─────────┘ (direct A–C is longer than A–B–C)
    private static RouteGraph Sample() => RouteGraph.Build(
        ["a", "b", "c", "d", "e"],
        [("a", "b", 9), ("b", "c", 4), ("c", "d", 3), ("a", "c", 20)]);

    [Theory]
    [InlineData("a", "b", 9)]
    [InlineData("a", "c", 13)]   // via b (9+4) beats the direct 20
    [InlineData("a", "d", 16)]
    [InlineData("d", "a", 16)]   // undirected: same both ways
    [InlineData("b", "b", 0)]
    public void Finds_shortest_distance(string from, string to, long expected)
    {
        Sample().TryGetDistance(from, to, out long metres).ShouldBeTrue();
        metres.ShouldBe(expected);
    }

    [Fact]
    public void Isolated_station_is_unreachable()
    {
        Sample().TryGetDistance("a", "e", out _).ShouldBeFalse();
    }

    [Fact]
    public void Unknown_station_returns_false()
    {
        Sample().TryGetDistance("a", "zzz", out _).ShouldBeFalse();
    }

    [Fact]
    public void Edge_to_unknown_station_is_rejected_at_build()
    {
        Should.Throw<ArgumentException>(() => RouteGraph.Build(["a"], [("a", "ghost", 5)]));
    }

    [Fact]
    public void Non_positive_distance_is_rejected_at_build()
    {
        // Dijkstra is only correct with positive weights.
        Should.Throw<ArgumentException>(() => RouteGraph.Build(["a", "b"], [("a", "b", 0)]));
    }

    [Fact]
    public void Empty_network_builds()
    {
        RouteGraph.Build([], []).StationCount.ShouldBe(0);
    }
}

public sealed class SlabFarePolicyTests
{
    [Theory]
    [InlineData(1, 2_000)]
    [InlineData(2_000, 2_000)]     // exactly on a limit stays in that slab
    [InlineData(2_001, 3_000)]     // one metre over moves up
    [InlineData(9_000, 6_000)]
    [InlineData(16_000, 8_000)]
    [InlineData(16_500, 10_000)]
    [InlineData(1_000_000, 10_000)] // beyond the last limit → top slab
    public void Picks_the_right_slab(long metres, long poisha)
    {
        new SlabFarePolicy().FareFor(metres).ShouldBe(poisha);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Rejects_non_positive_distance(long metres)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new SlabFarePolicy().FareFor(metres));
    }
}

public sealed class FareQuoteRulesTests
{
    [Fact]
    public void Disconnected_stations_give_no_route_business_error()
    {
        var graph = RouteGraph.Build(["a", "b", "island"], [("a", "b", 1_000)]);

        var result = TransitPass.Network.Api.Fares.FareEndpoints.Quote(graph, new SlabFarePolicy(), "a", "island");

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("fare.no_route");
        result.Error.Type.ShouldBe(TransitPass.BuildingBlocks.Results.ErrorType.BusinessRule); // → 422
    }
}
