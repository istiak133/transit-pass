using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TransitPass.BuildingBlocks.Hosting;

/// <summary>
/// Gives every request an <c>X-Request-Id</c>. The gateway creates it, YARP forwards it to the service, and both log it,
/// so one id links the gateway log line with the service log line for the same call.
/// The id is always a UUID in 32-hex "N" form. An incoming value is kept only if it parses as one, and the
/// kept value is re-printed from the parsed <see cref="Guid"/>, so no client-written text ever reaches the logs.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>The header name.</summary>
    public const string HeaderName = "X-Request-Id";

    /// <summary>Runs the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string requestId = Normalize(context.Request.Headers[HeaderName]);

        // Overwrite the request header, so the proxied request carries the cleaned value.
        context.Request.Headers[HeaderName] = requestId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = requestId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId }))
        {
            await next(context);
        }
    }

    // O(1): a UUID has a fixed length, so parsing is constant time. Anything else gets a fresh UUIDv7,
    // which is time ordered, so ids sort roughly by when the request arrived.
    internal static string Normalize(string? incoming) =>
        Guid.TryParseExact(incoming, "N", out var parsed) && parsed != Guid.Empty
            ? parsed.ToString("N")
            : Guid.CreateVersion7().ToString("N");
}
