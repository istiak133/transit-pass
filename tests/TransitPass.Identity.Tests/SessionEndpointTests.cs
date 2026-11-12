using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TransitPass.Identity.Tests;

public sealed class SessionEndpointTests(IdentityFactory factory) : IClassFixture<IdentityFactory>
{
    private const string Login = "/api/v1/identity/sessions";
    private readonly HttpClient _client = factory.CreateClient();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewPhone() => "018" + Random.Shared.Next(10_000_000, 99_999_999);

    private async Task<string> RegisterAsync(string phone, string password = "s3cret-pass")
    {
        var r = await _client.PostAsJsonAsync("/api/v1/identity/users", new { phone, name = "Rafi", password }, Ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
    }

    private Task<HttpResponseMessage> LoginAsync(string? phone, string? password) =>
        _client.PostAsJsonAsync(Login, new { phone, password }, Ct);

    [Fact]
    public async Task Login_returns_es256_token_that_verifies_with_the_published_jwks()
    {
        string phone = NewPhone();
        string userId = await RegisterAsync(phone);

        var response = await LoginAsync(phone, "s3cret-pass");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("tokenType").GetString().ShouldBe("Bearer");
        body.GetProperty("expiresIn").GetInt32().ShouldBe(900);
        string token = body.GetProperty("accessToken").GetString()!;

        // Verify the way another service will: only the public key from JWKS, standard library.
        string jwks = await _client.GetStringAsync("/.well-known/jwks.json", Ct);
        var keys = new JsonWebKeySet(jwks).GetSigningKeys();
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "transitpass-identity",
            ValidAudience = "transitpass",
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        });

        result.IsValid.ShouldBeTrue(result.Exception?.Message);
        var jwt = (JsonWebToken)result.SecurityToken;
        jwt.Alg.ShouldBe("ES256");
        jwt.Kid.ShouldBe(keys.Single().KeyId);
        jwt.Subject.ShouldBe(userId);
        (jwt.ValidTo - jwt.IssuedAt).ShouldBe(TimeSpan.FromMinutes(15));
        jwt.Claims.ShouldNotContain(c => c.Type == "phone"); // no personal data inside a readable token
    }

    [Fact]
    public async Task A_tampered_token_fails_verification()
    {
        string phone = NewPhone();
        await RegisterAsync(phone);
        var body = await (await LoginAsync(phone, "s3cret-pass")).Content.ReadFromJsonAsync<JsonElement>(Ct);
        string[] parts = body.GetProperty("accessToken").GetString()!.Split('.');

        // Swap the payload for one claiming to be someone else, keep the original signature.
        string forged = Base64UrlEncoder.Encode($$"""{"sub":"{{Guid.NewGuid()}}","iss":"transitpass-identity","aud":"transitpass","exp":4102444800}""");
        string tampered = $"{parts[0]}.{forged}.{parts[2]}";

        var keys = new JsonWebKeySet(await _client.GetStringAsync("/.well-known/jwks.json", Ct)).GetSigningKeys();
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(tampered, new TokenValidationParameters
        {
            ValidIssuer = "transitpass-identity",
            ValidAudience = "transitpass",
            IssuerSigningKeys = keys,
        });

        result.IsValid.ShouldBeFalse();
        result.Exception.ShouldBeAssignableTo<SecurityTokenInvalidSignatureException>();
    }

    [Fact]
    public async Task Login_accepts_the_phone_in_another_format()
    {
        string phone = NewPhone();
        await RegisterAsync(phone);
        (await LoginAsync("+880" + phone[1..], "s3cret-pass")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_phone_get_the_identical_answer()
    {
        string phone = NewPhone();
        await RegisterAsync(phone);

        var wrong = await LoginAsync(phone, "not-the-password");
        var unknown = await LoginAsync(NewPhone(), "not-the-password");
        var garbage = await LoginAsync("hello", "not-the-password");

        foreach (var r in new[] { wrong, unknown, garbage })
        {
            r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var p = await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
            p.GetProperty("code").GetString().ShouldBe("identity.invalid_credentials");
            p.GetProperty("title").GetString().ShouldBe("Phone or password is incorrect.");
        }
    }

    [Fact]
    public async Task Sixth_attempt_in_a_minute_for_one_phone_is_429_across_formats()
    {
        string phone = NewPhone();
        await RegisterAsync(phone);

        for (int i = 0; i < 5; i++)
        {
            // Mix formats: they must share one counter.
            string asTyped = i % 2 == 0 ? phone : "+880" + phone[1..];
            (await LoginAsync(asTyped, "wrong-password")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var blocked = await LoginAsync(phone, "s3cret-pass"); // even the right password waits now
        blocked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        blocked.Headers.RetryAfter.ShouldNotBeNull();
        (await blocked.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("identity.too_many_attempts");
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("01711000001", null)]
    [InlineData("  ", "")]
    public async Task Missing_fields_are_400(string? phone, string? password)
    {
        (await LoginAsync(phone, password)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Jwks_publishes_only_the_public_key_and_is_cacheable()
    {
        var response = await _client.GetAsync("/.well-known/jwks.json", Ct);
        response.Headers.CacheControl!.MaxAge.ShouldBe(TimeSpan.FromMinutes(5));

        var key = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("keys")[0];
        key.GetProperty("kty").GetString().ShouldBe("EC");
        key.GetProperty("crv").GetString().ShouldBe("P-256");
        key.GetProperty("alg").GetString().ShouldBe("ES256");
        key.TryGetProperty("d", out _).ShouldBeFalse(); // the private part must never leave Identity
    }
}
