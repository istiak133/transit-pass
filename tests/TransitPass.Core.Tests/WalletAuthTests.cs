using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TransitPass.Core.Tests;

public sealed class WalletAuthTests
{
    private const string Me = "/api/v1/wallet/me";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpResponseMessage> GetMe(HttpClient client, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Me);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task ShouldBe401(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldContain(h => h.Scheme == "Bearer");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("auth.unauthenticated");
        problem.TryGetProperty("traceId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task No_token_is_401_problem_with_www_authenticate()
    {
        await using var factory = new CoreFactory();
        await ShouldBe401(await GetMe(factory.CreateClient(), null));
    }

    [Fact]
    public async Task Valid_token_is_200_and_me_is_the_token_subject()
    {
        await using var factory = new CoreFactory();
        Guid userId = Guid.CreateVersion7();

        var response = await GetMe(factory.CreateClient(), factory.Identity.Token(sub: userId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("userId").GetGuid().ShouldBe(userId);
    }

    [Fact]
    public async Task Keys_are_fetched_once_and_cached_across_requests()
    {
        await using var factory = new CoreFactory();
        var client = factory.CreateClient();

        for (int i = 0; i < 10; i++)
        {
            (await GetMe(client, factory.Identity.Token())).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        factory.Identity.Fetches.ShouldBe(1); // no call to Identity per request
    }

    public static TheoryData<string> BadTokens => new()
    {
        "expired",
        "wrong-audience",
        "wrong-issuer",
        "unknown-key",
        "tampered-payload",
        "alg-none",
        "hs256",
        "sub-not-a-user-id",
        "garbage",
    };

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task Bad_tokens_are_401(string kind)
    {
        await using var factory = new CoreFactory();
        FakeIdentity id = factory.Identity;

        string token = kind switch
        {
            "expired" => id.Token(expiresIn: TimeSpan.FromMinutes(-2)),          // beyond the 30 s clock skew
            "wrong-audience" => id.Token(audience: "someone-else"),
            "wrong-issuer" => id.Token(issuer: "evil-issuer"),
            "unknown-key" => id.Token(key: FakeIdentity.NewKey()),               // right shape, not Identity's key
            "tampered-payload" => Tamper(id.Token()),
            "alg-none" => Unsigned(),
            "hs256" => Hs256(),
            "sub-not-a-user-id" => id.Token(subject: "admin"),
            _ => "not.a.jwt",
        };

        await ShouldBe401(await GetMe(factory.CreateClient(), token));
    }

    [Fact]
    public async Task Rotated_key_is_picked_up_after_one_refresh()
    {
        await using var factory = new CoreFactory();
        var client = factory.CreateClient();
        (await GetMe(client, factory.Identity.Token())).StatusCode.ShouldBe(HttpStatusCode.OK);

        factory.Identity.Rotate();                 // Identity starts signing with a new key, new kid
        string fresh = factory.Identity.Token();
        await Task.Delay(TimeSpan.FromSeconds(1.2), Ct); // past the minimum refresh gap

        // The first request with the unknown kid asks the key cache to refresh; within a moment it verifies.
        HttpStatusCode status = HttpStatusCode.Unauthorized;
        for (int i = 0; i < 10 && status != HttpStatusCode.OK; i++)
        {
            status = (await GetMe(client, fresh)).StatusCode;
            if (status != HttpStatusCode.OK)
            {
                await Task.Delay(200, Ct);
            }
        }

        status.ShouldBe(HttpStatusCode.OK);
        factory.Identity.Fetches.ShouldBe(2);
    }

    [Fact]
    public async Task A_flood_of_unknown_kids_does_not_flood_identity()
    {
        await using var factory = new CoreFactory();
        var client = factory.CreateClient();
        (await GetMe(client, factory.Identity.Token())).StatusCode.ShouldBe(HttpStatusCode.OK);

        for (int i = 0; i < 30; i++)
        {
            await ShouldBe401(await GetMe(client, factory.Identity.Token(key: FakeIdentity.NewKey())));
        }

        factory.Identity.Fetches.ShouldBeLessThanOrEqualTo(2); // the 1 s minimum gap caps re-fetches
    }

    [Fact]
    public async Task Identity_down_after_keys_are_cached_still_verifies()
    {
        await using var factory = new CoreFactory();
        var client = factory.CreateClient();
        (await GetMe(client, factory.Identity.Token())).StatusCode.ShouldBe(HttpStatusCode.OK);

        factory.Identity.Down = true;
        await Task.Delay(TimeSpan.FromSeconds(1.2), Ct);
        await GetMe(client, factory.Identity.Token(key: FakeIdentity.NewKey())); // forces a refresh, which fails

        (await GetMe(client, factory.Identity.Token())).StatusCode.ShouldBe(HttpStatusCode.OK); // last good keys kept
    }

    private static string Tamper(string token)
    {
        string[] p = token.Split('.');
        string other = Base64UrlEncoder.Encode($$"""{"sub":"{{Guid.NewGuid()}}","iss":"transitpass-identity","aud":"transitpass","exp":4102444800}""");
        return $"{p[0]}.{other}.{p[2]}";
    }

    // The classic "alg: none" attack: an unsigned token that claims to need no signature.
    private static string Unsigned()
    {
        string header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        string payload = Base64UrlEncoder.Encode($$"""{"sub":"{{Guid.NewGuid()}}","iss":"transitpass-identity","aud":"transitpass","exp":4102444800}""");
        return $"{header}.{payload}.";
    }

    // A token signed with a shared secret (HS256): must be refused even if the secret were guessed.
    private static string Hs256() => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "transitpass-identity",
        Audience = "transitpass",
        Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString() },
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('k', 64))), SecurityAlgorithms.HmacSha256),
    });
}
