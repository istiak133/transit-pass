using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Forwarder;

namespace TransitPass.Gateway.Tests;

/// <summary>
/// Hosts the real gateway in memory, but replaces the HTTP client YARP uses to reach services
/// with a stub that records the forwarded request. No service has to run.
/// </summary>
public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings = new()
    {
        ["ReverseProxy:Clusters:identity:Destinations:primary:Address"] = "http://identity.test/",
        ["ReverseProxy:Clusters:network:Destinations:primary:Address"] = "http://network.test/",
        ["ReverseProxy:Clusters:core:Destinations:primary:Address"] = "http://core.test/",
        ["RateLimiting:TokenLimit"] = "1000",
        ["RateLimiting:TokensPerPeriod"] = "1000",
    };

    /// <summary>Every request the gateway forwarded, in order.</summary>
    public ConcurrentQueue<HttpRequestMessage> Forwarded { get; } = new();

    /// <summary>Overrides a setting before the host starts.</summary>
    public GatewayFactory With(string key, string value)
    {
        _settings[key] = value;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(_settings));
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IForwarderHttpClientFactory>(new StubClientFactory(Forwarded)));
    }

    private sealed class StubClientFactory(ConcurrentQueue<HttpRequestMessage> log) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new StubHandler(log));
    }

    private sealed class StubHandler(ConcurrentQueue<HttpRequestMessage> log) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            log.Enqueue(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{request.RequestUri!.Host}{request.RequestUri.PathAndQuery}"),
            });
        }
    }
}
