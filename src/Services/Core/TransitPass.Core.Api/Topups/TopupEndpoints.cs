using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TransitPass.BuildingBlocks.Auth;
using TransitPass.BuildingBlocks.Http;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Core.Api.Data;
using TransitPass.Core.Api.Payments;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Api.Topups;

/// <summary>Top-up endpoints under /api/v1/wallet/topups (protected).</summary>
public static class TopupEndpoints
{
    internal const long MinAmountPoisha = 10_00;       // ৳10
    internal const long MaxAmountPoisha = 50_000_00;   // ৳50,000

    /// <summary>Maps the top-up routes.</summary>
    public static IEndpointRouteBuilder MapTopupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/wallet/topups").RequireAuthorization();

        // POST → 201 { id, status: "Pending", checkoutUrl, … }. No money moves here: the browser
        // could lie about paying. Money moves only when the provider's signed webhook arrives.
        group.MapPost("/", async (
            CreateTopupRequest body, ClaimsPrincipal user, CoreDbContext db, TimeProvider clock,
            IOptions<PaymentOptions> options, CancellationToken ct) =>
        {
            var created = await CreateAsync(body, user.GetUserId(), db, clock.GetUtcNow(), ct);
            if (!created.IsSuccess)
            {
                return created.Error.ToProblem();
            }

            var response = ToResponse(created.Value, options.Value);
            return TypedResults.Created($"/api/v1/wallet/topups/{response.Id}", response);
        });

        // GET /{id} → the caller's own top-up. Someone else's id is 404, not 403, so ids can't be probed.
        group.MapGet("/{id:guid}", async (
            Guid id, ClaimsPrincipal user, CoreDbContext db, IOptions<PaymentOptions> options, CancellationToken ct) =>
        {
            Guid userId = user.GetUserId();
            Topup? topup = await db.Topups.AsNoTracking()
                .Where(t => t.Id == id && db.Wallets.Any(w => w.Id == t.WalletId && w.UserId == userId))
                .SingleOrDefaultAsync(ct);
            return topup is null
                ? Error.NotFound("topup.not_found", "No top-up with this id.").ToProblem()
                : TypedResults.Ok(ToResponse(topup, options.Value));
        });

        return app;
    }

    internal static async Task<Result<Topup>> CreateAsync(
        CreateTopupRequest body, Guid userId, CoreDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (body.AmountPoisha is not (>= MinAmountPoisha and <= MaxAmountPoisha))
        {
            return Error.Validation("topup.invalid_amount",
                $"amountPoisha must be between {MinAmountPoisha} (৳10) and {MaxAmountPoisha} (৳50,000).");
        }

        Wallet wallet = await WalletEndpoints.GetOrCreateAsync(db, userId, now, ct);
        var topup = new Topup
        {
            Id = Guid.CreateVersion7(now),
            WalletId = wallet.Id,
            AmountPoisha = body.AmountPoisha.Value,
            Status = TopupStatus.Pending,
            CreatedAt = now,
        };
        db.Topups.Add(topup);
        await db.SaveChangesAsync(ct);
        return topup;
    }

    internal static TopupResponse ToResponse(Topup t, PaymentOptions o) =>
        new(t.Id, t.AmountPoisha, t.Status.ToString(), t.CreatedAt, t.CompletedAt, o.CheckoutBaseUrl + t.Id);
}
