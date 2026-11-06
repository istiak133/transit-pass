using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;

namespace TransitPass.Gateway.Tests;

public sealed class GatewayRoutingTests
{
    [Theory]
    [InlineData("/api/v1/identity/users/me", "identity.test")]
    [InlineData("/api/v1/network/stations?limit=5", "network.test")]
    [InlineData("/api/v1/wallet/balance", "core.test")]
    [InlineData("/api/v1/tickets/abc", "core.test")]
    [InlineData("/api/v1/gate/taps", "core.test")]
    public async Task Route_forwards_to_the_owning_service_with_path_and_query_intact(string path, string expectedHost)
    {
        using var factory = new GatewayFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe(expectedHost + path);
    }

    [Fact]
    public async Task Unknown_path_is_a_problem_404_and_is_not_forwarded()
    {
        using var factory = new GatewayFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/unknown", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        factory.Forwarded.ShouldBeEmpty();
    }

    [Fact]
    public async Task Request_id_is_generated_forwarded_and_echoed()
    {
        using var factory = new GatewayFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/identity/ping", UriKind.Relative), TestContext.Current.CancellationToken);

        string echoed = response.Headers.GetValues("X-Request-Id").Single();
        echoed.Length.ShouldBe(32);
        factory.Forwarded.Single().Headers.GetValues("X-Request-Id").Single().ShouldBe(echoed);
    }

    [Fact]
    public async Task Safe_client_request_id_is_kept_and_unsafe_one_replaced()
    {
        using var factory = new GatewayFactory();
        using var client = factory.CreateClient();

        using var safe = new HttpRequestMessage(HttpMethod.Get, "/api/v1/identity/ping");
        safe.Headers.Add("X-Request-Id", "client-abc_123");
        var kept = await client.SendAsync(safe, TestContext.Current.CancellationToken);

        using var unsafeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/identity/ping");
        unsafeRequest.Headers.TryAddWithoutValidation("X-Request-Id", "evil value; injected");
        var replaced = await client.SendAsync(unsafeRequest, TestContext.Current.CancellationToken);

        kept.Headers.GetValues("X-Request-Id").Single().ShouldBe("client-abc_123");
        replaced.Headers.GetValues("X-Request-Id").Single().ShouldNotContain("evil");
    }

    [Fact]
    public async Task Client_over_the_limit_gets_429_problem_with_retry_after()
    {
        using var factory = new GatewayFactory()
            .With("RateLimiting:TokenLimit", "2")
            .With("RateLimiting:TokensPerPeriod", "1")
            .With("RateLimiting:ReplenishmentPeriod", "00:01:00");
        using var client = factory.CreateClient();
        var uri = new Uri("/api/v1/network/stations", UriKind.Relative);

        (await client.GetAsync(uri, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(uri, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var third = await client.GetAsync(uri, TestContext.Current.CancellationToken);

        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        third.Headers.RetryAfter.ShouldNotBeNull();
        var problem = await third.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        problem!.Extensions["code"]!.ToString().ShouldBe("gateway.rate_limited");
        factory.Forwarded.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Liveness_is_answered_by_the_gateway_itself()
    {
        using var factory = new GatewayFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Forwarded.ShouldBeEmpty();
    }
}
