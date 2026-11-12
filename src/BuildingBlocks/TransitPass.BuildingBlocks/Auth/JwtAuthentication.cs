using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace TransitPass.BuildingBlocks.Auth;

/// <summary>
/// Lets a service trust Identity's access tokens. Verification is local: the service fetches Identity's
/// public keys once (JWKS), caches them, and checks each token's signature and claims itself, without a
/// call to Identity per request.
/// </summary>
public static class JwtAuthentication
{
    /// <summary>Name of the HTTP client used to fetch JWKS. Tests replace its handler.</summary>
    public const string JwksHttpClient = "jwks";

    /// <summary>Registers JWT bearer authentication and authorization.</summary>
    public static WebApplicationBuilder AddJwtAuthentication(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Configure<JwtVerificationOptions>(builder.Configuration.GetSection(JwtVerificationOptions.Section));
        builder.Services.AddHttpClient(JwksHttpClient, c => c.Timeout = TimeSpan.FromSeconds(5));

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtVerificationOptions>, IHttpClientFactory>(Configure);

        builder.Services.AddAuthorization();
        return builder;
    }

    private static void Configure(JwtBearerOptions options, IOptions<JwtVerificationOptions> settings, IHttpClientFactory http)
    {
        JwtVerificationOptions o = settings.Value;

        // Keys: fetched from Identity, cached, refreshed every KeyRefreshInterval, and re-fetched early
        // (at most once per KeyRefreshMinInterval) when a token names a kid we don't have yet.
        options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            o.JwksUrl,
            new JwksRetriever(),
            new HttpDocumentRetriever(http.CreateClient(JwksHttpClient)) { RequireHttps = o.RequireHttps })
        {
            AutomaticRefreshInterval = o.KeyRefreshInterval,
            RefreshInterval = o.KeyRefreshMinInterval,
        };

        // Keep claim names as written in the token ("sub" stays "sub", not a long SOAP-era URI).
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = o.Issuer,
            ValidAudience = o.Audience,
            // Only ES256. A token saying "alg: none" or "HS256" is refused before any key is tried.
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = o.ClockSkew,
            NameClaimType = JwtRegisteredClaimNames.Sub,
        };

        options.Events = new JwtBearerEvents
        {
            // Everything downstream relies on sub being a user id. Reject the token if it isn't.
            OnTokenValidated = ctx =>
            {
                if (!Guid.TryParse(ctx.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub), out _))
                {
                    ctx.Fail("The token's sub is not a user id.");
                }

                return Task.CompletedTask;
            },

            // 401: no token, or a token we don't accept. Same problem shape as every other error.
            OnChallenge = async ctx =>
            {
                ctx.HandleResponse(); // we write the response ourselves
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = ctx.AuthenticateFailure is null
                    ? "Bearer"
                    : "Bearer error=\"invalid_token\"";
                await WriteProblemAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized,
                    "auth.unauthenticated", "A valid access token is required.");
            },

            // 403: we know who you are, but this isn't allowed for you.
            OnForbidden = ctx => WriteProblemAsync(ctx.HttpContext, StatusCodes.Status403Forbidden,
                "auth.forbidden", "You are not allowed to do this."),
        };
    }

    private static Task WriteProblemAsync(HttpContext http, int status, string code, string title)
    {
        http.Response.StatusCode = status;
        return TypedResults.Problem(
            statusCode: status,
            title: title,
            type: $"https://transitpass.dev/errors/{code}",
            extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(http);
    }

    /// <summary>The caller's user id (the token's <c>sub</c>). Only valid on endpoints that require authorization.</summary>
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
    }
}
