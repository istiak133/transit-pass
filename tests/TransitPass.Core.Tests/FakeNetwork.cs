using System.Net;
using System.Text;
using System.Text.Json;

namespace TransitPass.Core.Tests;

/// <summary>How the fake Network service behaves on the next calls.</summary>
public enum NetworkMode
{
    Ok,
    StationNotFound,
    SameStation,
    NoRoute,
    ServerError,
    ConnectionRefused,
    Timeout,
}

/// <summary>A stand-in for Network's GET /api/v1/network/fares/quote.</summary>
public sealed class FakeNetwork : HttpMessageHandler
{
    private int _calls;

    public NetworkMode Mode { get; set; } = NetworkMode.Ok;

    /// <summary>The fare every Ok quote returns. ৳100 by default.</summary>
    public long FarePoisha { get; set; } = 100_00;

    public int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Mode switch
        {
            NetworkMode.ConnectionRefused => throw new HttpRequestException("Connection refused"),
            NetworkMode.Timeout => throw new TaskCanceledException("The request timed out"),
            _ => Task.FromResult(Respond(request.RequestUri!)),
        };
    }

    // The caller (HttpClient) owns and disposes the response.
    private HttpResponseMessage Respond(Uri uri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        string from = query["from"]!, to = query["to"]!;
        return Mode switch
        {
            NetworkMode.Ok => Json(HttpStatusCode.OK, new { from, to, distanceM = 16_500, farePoisha = FarePoisha }),
            NetworkMode.StationNotFound => Problem(HttpStatusCode.NotFound, "station.not_found", $"Station '{to}' does not exist."),
            NetworkMode.SameStation => Problem(HttpStatusCode.BadRequest, "fare.same_station", "'from' and 'to' must be different stations."),
            NetworkMode.NoRoute => Problem(HttpStatusCode.UnprocessableEntity, "fare.no_route", "There is no track."),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Problem(HttpStatusCode status, string code, string title) =>
        Json(status, new { type = $"https://transitpass.dev/errors/{code}", title, status = (int)status, code });
}
