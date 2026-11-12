using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TransitPass.BuildingBlocks.Auth;
using TransitPass.BuildingBlocks.Http;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Core.Api.Data;
using TransitPass.Core.Api.Fares;

namespace TransitPass.Core.Api.Tickets;

/// <summary>Ticket endpoints under /api/v1/tickets (protected).</summary>
public static class TicketEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    /// <summary>Maps the ticket routes.</summary>
    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tickets").RequireAuthorization();

        // POST { from, to } + Idempotency-Key → 201 + Location. A retry with the same key → the same 201
        // with Idempotent-Replayed: true, and no second charge.
        group.MapPost("/", async (
            BuyTicketRequest body, [FromHeader(Name = IdempotencyHeader)] string? key, ClaimsPrincipal user,
            HttpContext http, CoreDbContext db, IFareQuotes fares, TimeProvider clock, CancellationToken ct) =>
        {
            var result = await TicketPurchase.PurchaseAsync(user.GetUserId(), key, body, db, fares, clock, ct);
            if (!result.IsSuccess)
            {
                return result.Error.ToProblem();
            }

            http.Response.Headers.Location = $"/api/v1/tickets/{result.Value.TicketId}";
            if (result.Value.Replayed)
            {
                http.Response.Headers[ReplayedHeader] = "true";
            }

            return Results.Content(result.Value.ResponseJson, "application/json", Encoding.UTF8, StatusCodes.Status201Created);
        });

        // GET /{id} → the caller's own ticket; anyone else's is 404 (ticket ids go into QR codes, so never confirm one).
        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, CoreDbContext db, CancellationToken ct) =>
        {
            Guid userId = user.GetUserId();
            Ticket? ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
            return ticket is null
                ? Error.NotFound("ticket.not_found", "No ticket with this id.").ToProblem()
                : TypedResults.Ok(ToResponse(ticket));
        });

        return app;
    }

    internal static TicketResponse ToResponse(Ticket t) =>
        new(t.Id, t.FromStation, t.ToStation, t.FarePoisha, t.DistanceM, t.Status.ToString(), t.PurchasedAt, t.ExpiresAt);
}
