using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TransitPass.BuildingBlocks.Auth;

namespace TransitPass.Core.Tests;

/// <summary>
/// Hosts Core in memory and plays Identity's part: it owns a signing key, serves the matching JWKS
/// through a stub HTTP handler, and can sign any token a test needs (valid, expired, forged…).
/// </summary>
public sealed class CoreFactory(PostgresFixture db) : WebApplicationFactory<Program>
{
    public FakeIdentity Identity { get; } = new();

    /// <summary>A client that sends a valid token for <paramref name="userId"/> on every request.</summary>
    public HttpClient ClientFor(Guid userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Identity.Token(sub: userId));
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Identity.Dispose();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", db.ConnectionString); // already migrated by the fixture
        builder.UseSetting("Jwt:JwksUrl", "http://identity.test/.well-known/jwks.json");
        builder.UseSetting("Jwt:RequireHttps", "false");
        builder.UseSetting("Jwt:KeyRefreshMinInterval", "00:00:01"); // the library's minimum
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(JwtAuthentication.JwksHttpClient).ConfigurePrimaryHttpMessageHandler(() => Identity));
    }
}

/// <summary>A stand-in for the Identity service's key and JWKS endpoint.</summary>
public sealed class FakeIdentity : HttpMessageHandler
{
    private readonly List<ECDsa> _owned = []; // every key this fake creates, disposed with it
    private int _fetches;

    public FakeIdentity() => SigningKey = NewKey();

    /// <summary>The key currently published (and used by <see cref="Token"/> by default).</summary>
    public ECDsaSecurityKey SigningKey { get; private set; }

    /// <summary>When true, the JWKS endpoint answers 503, as if Identity were down.</summary>
    public bool Down { get; set; }

    /// <summary>How many times Core fetched the JWKS.</summary>
    public int Fetches => Volatile.Read(ref _fetches);

    /// <summary>Publishes a brand-new key, as Identity does when it rotates.</summary>
    public ECDsaSecurityKey Rotate() => SigningKey = NewKey();

    /// <summary>A fresh P-256 key that this fake owns (and disposes).</summary>
    public ECDsaSecurityKey NewKey()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _owned.Add(ecdsa);
        var key = new ECDsaSecurityKey(ecdsa);
        key.KeyId = Base64UrlEncoder.Encode(key.ComputeJwkThumbprint());
        return key;
    }

    /// <summary>Signs a token. Defaults match what Identity issues; tests override one thing at a time.</summary>
    public string Token(
        Guid? sub = null, string issuer = "transitpass-identity", string audience = "transitpass",
        TimeSpan? expiresIn = null, SecurityKey? key = null, string? subject = null)
    {
        DateTime now = DateTime.UtcNow;
        DateTime expires = now + (expiresIn ?? TimeSpan.FromMinutes(15));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", subject ?? (sub ?? Guid.NewGuid()).ToString())]),
            IssuedAt = expires < now ? expires.AddMinutes(-15) : now,
            NotBefore = expires < now ? expires.AddMinutes(-15) : now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.EcdsaSha256),
        });
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _fetches);
        return Task.FromResult(Respond());
    }

    // The caller (HttpClient) owns and disposes the response.
    private HttpResponseMessage Respond()
    {
        if (Down)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        JsonWebKey jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(SigningKey);
        string json = $$"""{"keys":[{"kty":"EC","crv":"{{jwk.Crv}}","alg":"ES256","use":"sig","kid":"{{SigningKey.KeyId}}","x":"{{jwk.X}}","y":"{{jwk.Y}}"}]}""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _owned.ForEach(k => k.Dispose());
            _owned.Clear();
        }

        base.Dispose(disposing);
    }
}
