using System.Security.Claims;
using TransitPass.BuildingBlocks.Auth;

namespace TransitPass.Core.Api.Wallet;

/// <summary>Who the caller is, as Core sees it. Phase 7 adds the balance.</summary>
public sealed record WalletOwnerResponse(Guid UserId);

/// <summary>Wallet endpoints under /api/v1/wallet. Every route here requires a valid access token.</summary>
public static class WalletEndpoints
{
    /// <summary>Maps the wallet routes.</summary>
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/wallet").RequireAuthorization();

        // GET /api/v1/wallet/me → 200 { userId } with a valid token, 401 without one.
        // "me" comes from the token's sub, never from the URL or body, so a caller can't ask for someone else.
        group.MapGet("/me", (ClaimsPrincipal user) => TypedResults.Ok(new WalletOwnerResponse(user.GetUserId())));

        return app;
    }
}
