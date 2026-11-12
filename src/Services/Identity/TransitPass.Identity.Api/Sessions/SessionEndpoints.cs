using Microsoft.EntityFrameworkCore;
using TransitPass.BuildingBlocks.Http;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Identity.Api.Data;
using TransitPass.Identity.Api.Users;

namespace TransitPass.Identity.Api.Sessions;

/// <summary>Login request body.</summary>
public sealed record LoginRequest(string? Phone, string? Password);

/// <summary>A successful login. <c>expiresIn</c> is in seconds, as in OAuth 2.0.</summary>
public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, DateTimeOffset ExpiresAt);

/// <summary>Login (POST /api/v1/identity/sessions) and the public keys (GET /.well-known/jwks.json).</summary>
public static class SessionEndpoints
{
    // A real PBKDF2 hash of nothing in particular. When the phone has no account we still verify against it,
    // so "unknown phone" takes as long as "wrong password" and timing can't reveal which phones exist.
    private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash("timing-equalizer"));

    private static readonly Error InvalidCredentials =
        Error.Unauthorized("identity.invalid_credentials", "Phone or password is incorrect.");

    /// <summary>Maps the session and key routes.</summary>
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/identity/sessions", async (
            LoginRequest body, IdentityDbContext db, TokenIssuer issuer, LoginThrottle throttle, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Phone) || string.IsNullOrEmpty(body.Password))
            {
                return Error.Validation("identity.invalid_request", "Phone and password are required.").ToProblem();
            }

            // Throttle by the normalized phone, so "017…" and "+88017…" share one counter.
            string key = PhoneNumber.TryNormalize(body.Phone, out var e164) ? e164 : body.Phone.Trim();
            if (!throttle.TryAcquire(key, out string? retryAfter))
            {
                http.Response.Headers.RetryAfter = retryAfter;
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too many login attempts. Try again later.",
                    type: "https://transitpass.dev/errors/identity.too_many_attempts",
                    extensions: new Dictionary<string, object?> { ["code"] = "identity.too_many_attempts" });
            }

            var result = await LoginAsync(e164, body.Password, db, issuer, ct);
            return result.ToHttp();
        });

        // Public keys for verifiers. Safe to cache briefly; a rotated key appears here before it signs anything.
        app.MapGet("/.well-known/jwks.json", (SigningKey key, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=300";
            return TypedResults.Ok(new { keys = new[] { key.PublicJwk() } });
        });

        return app;
    }

    internal static async Task<Result<TokenResponse>> LoginAsync(
        string? e164, string password, IdentityDbContext db, TokenIssuer issuer, CancellationToken ct)
    {
        // One indexed lookup (ux_users_phone). An invalid phone can't belong to anyone, so skip the query,
        // but still answer exactly like a wrong password.
        var user = e164 is null
            ? null
            : await db.Users.AsNoTracking()
                .Where(u => u.Phone == e164)
                .Select(u => new { u.Id, u.PasswordHash })
                .FirstOrDefaultAsync(ct);

        // Always run one PBKDF2 verification, against the real hash or the dummy, so both paths cost the same.
        bool passwordMatches = PasswordHasher.Verify(password, user?.PasswordHash ?? DummyHash.Value);
        if (user is null || !passwordMatches)
        {
            return InvalidCredentials;
        }

        var (token, expiresAt) = issuer.Issue(user.Id);
        return new TokenResponse(token, "Bearer", (int)issuer.Lifetime.TotalSeconds, expiresAt);
    }
}
