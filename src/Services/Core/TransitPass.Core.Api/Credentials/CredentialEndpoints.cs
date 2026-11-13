using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TransitPass.BuildingBlocks.Auth;
using TransitPass.BuildingBlocks.Http;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Core.Api.Data;
using TransitPass.Core.Api.Tickets;

namespace TransitPass.Core.Api.Credentials;

/// <summary>What the app turns into a QR code.</summary>
public sealed record CredentialResponse(Guid TicketId, string Credential, DateTimeOffset ExpiresAt);

/// <summary>
/// GET /api/v1/tickets/{id}/credential (protected): the signed QR credential for the caller's ticket.
/// GET /api/v1/gate/.well-known/jwks.json (public): the QR public keys gates verify with.
/// </summary>
public static class CredentialEndpoints
{
    public const string JwksPath = "/api/v1/gate/.well-known/jwks.json";

    /// <summary>Maps the credential and key routes.</summary>
    public static IEndpointRouteBuilder MapCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/tickets/{id:guid}/credential", async (
            Guid id, ClaimsPrincipal user, HttpContext http, CoreDbContext db,
            TicketCredentialIssuer issuer, TimeProvider clock, CancellationToken ct) =>
        {
            var result = await IssueAsync(user.GetUserId(), id, db, issuer, clock, ct);
            // A credential is as good as the ticket itself: no browser or proxy may keep a copy.
            http.Response.Headers.CacheControl = "no-store";
            return result.ToHttp();
        }).RequireAuthorization();

        // Public on purpose: these are public keys. Gates download them once and cache them, then verify every
        // QR offline. A rotated key is published here (as a retired key) for as long as its QR codes can live.
        app.MapGet(JwksPath, (QrKeys keys, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=300";
            return TypedResults.Ok(keys.Jwks);
        });

        return app;
    }

    /// <summary>One indexed read by primary key (plus the owner check), then an in-memory ES256 signature.</summary>
    internal static async Task<Result<CredentialResponse>> IssueAsync(
        Guid userId, Guid ticketId, CoreDbContext db, TicketCredentialIssuer issuer, TimeProvider clock, CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        Ticket? ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == ticketId && t.UserId == userId, ct);

        return ticket switch
        {
            // Not there, or someone else's: the same 404 (never confirm another user's ticket id).
            null => Error.NotFound("ticket.not_found", "No ticket with this id."),

            // Active (to enter) or InJourney (to exit), and still inside its service day: sign it.
            { Status: TicketStatus.Active or TicketStatus.InJourney } when ticket.ExpiresAt > now =>
                new CredentialResponse(ticket.Id, issuer.Issue(ticket, now), ticket.ExpiresAt),

            // Still marked Active (the expiry job is Phase 13), but the day is over. A QR would be born expired.
            { Status: TicketStatus.Active or TicketStatus.InJourney } => Error.Conflict(
                "ticket.expired", "This ticket's service day is over; it has no QR code any more."),

            _ => Error.Conflict(
                "ticket.not_usable", $"A ticket that is {ticket.Status} cannot be used at a gate, so it has no QR code."),
        };
    }
}
