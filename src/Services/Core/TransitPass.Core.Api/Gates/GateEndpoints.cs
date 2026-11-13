using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TransitPass.BuildingBlocks.Http;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Core.Api.Credentials;
using TransitPass.Core.Api.Data;
using TransitPass.Core.Api.Fares;
using TransitPass.Core.Api.Tickets;

namespace TransitPass.Core.Api.Gates;

/// <summary>What an open gate gets back (a closed gate gets a problem with the same fields and allowed: false).</summary>
public sealed record GateTapResponse(
    Guid TapId, bool Allowed, string Code, string Message, string Direction, Guid TicketId, string Station,
    string TicketStatus, long SurchargePoisha);

/// <summary>POST /api/v1/gate/taps: called by gate devices, not by users.</summary>
public static class GateEndpoints
{
    public const string TapsPath = "/api/v1/gate/taps";

    /// <summary>
    /// Maps the tap route. It has no user token: the caller is a gate, proven by its X-Gate-Key. The ticket holder
    /// is proven by the signed QR credential in the body.
    /// </summary>
    public static IEndpointRouteBuilder MapGateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(TapsPath, async (
            GateTapRequest body, [FromHeader(Name = GateOptions.KeyHeader)] string? key, HttpContext http,
            GateDevices gates, CoreDbContext db, TicketCredentialVerifier verifier, IFareQuotes fares,
            IStationDirectory stations, IOptions<GateOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            // 1. Which gate? Unknown gate and wrong key give the same 401, so ids can't be probed.
            GateDevice? gate = gates.Authenticate(body.GateId, key);
            if (gate is null)
            {
                return Error.Unauthorized("gate.unauthorized", "Unknown gate or wrong gate key.").ToProblem();
            }

            // 2. Is the tap well formed, from this gate's own station?
            var tap = GateTaps.Validate(body, gate);
            if (!tap.IsSuccess)
            {
                return tap.Error.ToProblem();
            }

            // 3. Decide.
            var outcome = await GateTaps.TapAsync(tap.Value, db, verifier, fares, stations, options.Value, clock, ct);
            if (!outcome.IsSuccess)
            {
                return outcome.Error.ToProblem();
            }

            if (outcome.Value.Replayed)
            {
                http.Response.Headers[TicketEndpoints.ReplayedHeader] = "true";
            }

            return Render(outcome.Value.Decision);
        }).AllowAnonymous();

        return app;
    }

    /// <summary>Open → 200 with the decision. Closed → an RFC 9457 problem (409/422) carrying the same fields.</summary>
    internal static IResult Render(GateDecision d)
    {
        if (d.Allowed)
        {
            return TypedResults.Ok(new GateTapResponse(
                d.TapId, true, d.Code, d.Message, d.Direction, d.TicketId, d.Station, d.TicketStatus, d.SurchargePoisha));
        }

        return TypedResults.Problem(
            statusCode: d.Status,
            title: d.Message,
            type: $"https://transitpass.dev/errors/{d.Code}",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = d.Code,
                ["allowed"] = false,
                ["tapId"] = d.TapId,
                ["direction"] = d.Direction,
                ["ticketId"] = d.TicketId,
                ["station"] = d.Station,
                ["ticketStatus"] = d.TicketStatus,
                ["surchargePoisha"] = d.SurchargePoisha,
            });
    }
}
