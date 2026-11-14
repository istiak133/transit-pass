using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TransitPass.BuildingBlocks.Http;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Identity.Api.Data;
using TransitPass.Identity.Api.Users;

namespace TransitPass.Identity.Api.Sessions;

/// <summary>Login request body.</summary>
public sealed record LoginRequest(string? Phone, string? Password);

/// <summary>Refresh and logout request body: the refresh token from the last login or refresh.</summary>
public sealed record RefreshRequest(string? RefreshToken);

/// <summary>
/// A successful login or refresh. <c>expiresIn</c> is the access token's lifetime in seconds, as in OAuth 2.0.
/// The refresh token is opaque and works once: keep only the newest one.
/// </summary>
public sealed record TokenResponse(
    string AccessToken, string TokenType, int ExpiresIn, DateTimeOffset ExpiresAt,
    string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

/// <summary>
/// Login (POST /api/v1/identity/sessions), refresh (POST …/sessions/refresh), logout (POST …/sessions/logout)
/// and the public keys (GET /.well-known/jwks.json).
/// </summary>
public static class SessionEndpoints
{
    // A real PBKDF2 hash of nothing in particular. When the phone has no account we still verify against it,
    // so "unknown phone" takes as long as "wrong password" and timing can't reveal which phones exist.
    private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash("timing-equalizer"));

    private static readonly Error InvalidCredentials =
        Error.Unauthorized("identity.invalid_credentials", "Phone or password is incorrect.");

    private static readonly Error MissingRefreshToken =
        Error.Validation("identity.invalid_request", "refreshToken is required.");

    /// <summary>Maps the session and key routes.</summary>
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/identity/sessions", async (
            LoginRequest body, IdentityDbContext db, TokenIssuer issuer, LoginThrottle throttle,
            IOptions<JwtOptions> options, TimeProvider clock, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Phone) || string.IsNullOrEmpty(body.Password))
            {
                return Error.Validation("identity.invalid_request", "Phone and password are required.").ToProblem();
            }

            // Throttle by the normalized phone, so "017…" and "+88017…" share one counter.
            string key = PhoneNumber.TryNormalize(body.Phone, out var e164) ? e164 : body.Phone.Trim();
            if (!throttle.TryAcquire(key, out string? retryAfter))
            {
                return TooManyAttempts(http, retryAfter, "Too many login attempts. Try again later.");
            }

            var result = await LoginAsync(e164, body.Password, db, issuer, options.Value, clock, ct);
            return result.ToHttp();
        });

        // Refresh: trade a refresh token for a new access token AND a new refresh token (rotation).
        // No access token needed: the refresh token is the credential, and the access token has usually expired.
        app.MapPost("/api/v1/identity/sessions/refresh", async (
            RefreshRequest body, IdentityDbContext db, TokenIssuer issuer, RefreshThrottle throttle,
            IOptions<JwtOptions> options, TimeProvider clock, ILogger<RefreshToken> logger, HttpContext http,
            CancellationToken ct) =>
        {
            if (Check(body, throttle, http) is { } rejected)
            {
                return rejected;
            }

            RefreshTokens.TryHash(body.RefreshToken, out byte[] hash);
            var rotated = await RefreshTokens.RotateAsync(
                db, hash, clock.GetUtcNow(), options.Value.RefreshTokenLifetime, logger, ct);
            if (!rotated.IsSuccess)
            {
                return rotated.Error.ToProblem();
            }

            var (access, expiresAt) = issuer.Issue(rotated.Value.UserId);
            var next = rotated.Value.Next;
            return TypedResults.Ok(new TokenResponse(
                access, "Bearer", (int)issuer.Lifetime.TotalSeconds, expiresAt, next.Token, next.ExpiresAt));
        });

        // Logout: end this login on this device (its whole refresh family). Access tokens already issued still
        // work until they expire (at most 15 minutes): a JWT can't be recalled, which is why it is short-lived.
        app.MapPost("/api/v1/identity/sessions/logout", async (
            RefreshRequest body, IdentityDbContext db, RefreshThrottle throttle, TimeProvider clock,
            HttpContext http, CancellationToken ct) =>
        {
            if (Check(body, throttle, http) is { } rejected)
            {
                return rejected;
            }

            RefreshTokens.TryHash(body.RefreshToken, out byte[] hash);
            var revoked = await RefreshTokens.RevokeFamilyAsync(db, hash, clock.GetUtcNow(), ct);
            return revoked.IsSuccess ? Results.NoContent() : revoked.Error.ToProblem();
        });

        // Public keys for verifiers. Safe to cache briefly; a rotated key appears here before it signs anything.
        app.MapGet("/.well-known/jwks.json", (SigningKey key, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=300";
            return TypedResults.Ok(new { keys = new[] { key.PublicJwk() } });
        });

        return app;
    }

    /// <summary>
    /// The checks refresh and logout share, in order: present (400), shaped like one of our tokens (401, no database
    /// work for garbage), and not hammered (429, keyed by the token's hash, so one token's loop can't block others).
    /// Null means "go ahead".
    /// </summary>
    private static IResult? Check(RefreshRequest body, RefreshThrottle throttle, HttpContext http)
    {
        if (string.IsNullOrEmpty(body.RefreshToken))
        {
            return MissingRefreshToken.ToProblem();
        }

        if (!RefreshTokens.TryHash(body.RefreshToken, out byte[] hash))
        {
            return RefreshTokens.InvalidToken.ToProblem();
        }

        return throttle.TryAcquire(Convert.ToHexString(hash), out string? retryAfter)
            ? null
            : TooManyAttempts(http, retryAfter, "Too many attempts with this refresh token. Try again later.");
    }

    private static ProblemHttpResult TooManyAttempts(HttpContext http, string? retryAfter, string title)
    {
        http.Response.Headers.RetryAfter = retryAfter;
        return TypedResults.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: title,
            type: "https://transitpass.dev/errors/identity.too_many_attempts",
            extensions: new Dictionary<string, object?> { ["code"] = "identity.too_many_attempts" });
    }

    internal static async Task<Result<TokenResponse>> LoginAsync(
        string? e164, string password, IdentityDbContext db, TokenIssuer issuer, JwtOptions options,
        TimeProvider clock, CancellationToken ct)
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

        // Every login starts its own refresh family: a phone and a laptop log out independently.
        var refresh = await RefreshTokens.StartFamilyAsync(db, user.Id, clock.GetUtcNow(), options.RefreshTokenLifetime, ct);
        var (token, expiresAt) = issuer.Issue(user.Id);
        return new TokenResponse(
            token, "Bearer", (int)issuer.Lifetime.TotalSeconds, expiresAt, refresh.Token, refresh.ExpiresAt);
    }
}
