using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TransitPass.BuildingBlocks.Hosting;

/// <summary>
/// Gives every request an <c>X-Request-Id</c>. The gateway creates it, YARP forwards it to the service, and both log it,
/// so one id links the gateway log line with the service log line for the same call.
/// A client-supplied id is kept only if it is short and safe, because it ends up in logs.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>The header name.</summary>
    public const string HeaderName = "X-Request-Id";

    private const int MaxLength = 64;

    /// <summary>Runs the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? incoming = context.Request.Headers[HeaderName];
        string requestId = IsSafe(incoming) ? incoming! : Guid.CreateVersion7().ToString("N");

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

    // O(length), at most 64 characters: letters, digits, '-' and '_' only, so the id cannot inject log lines.
    internal static bool IsSafe(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
