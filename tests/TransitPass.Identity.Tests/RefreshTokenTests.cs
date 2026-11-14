using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using TransitPass.Identity.Api.Sessions;

namespace TransitPass.Identity.Tests;

public sealed class RefreshTokenTests(IdentityFactory factory) : IClassFixture<IdentityFactory>
{
    private const string Login = "/api/v1/identity/sessions";
    private const string Refresh = "/api/v1/identity/sessions/refresh";
    private const string Logout = "/api/v1/identity/sessions/logout";
    private readonly HttpClient _client = factory.CreateClient();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewPhone() => "019" + Random.Shared.Next(10_000_000, 99_999_999);

    /// <summary>A well-formed token nobody issued: 32 fresh random bytes.</summary>
    private static string RandomToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static byte[] Sha256Of(string token) => SHA256.HashData(Base64Url.DecodeFromChars(token));

    private async Task<(string UserId, string Phone)> RegisterAsync()
    {
        string phone = NewPhone();
        var r = await _client.PostAsJsonAsync("/api/v1/identity/users", new { phone, name = "Rafi", password = "s3cret-pass" }, Ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        return ((await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!, phone);
    }

    private async Task<JsonElement> LoginAsync(string phone)
    {
        var r = await _client.PostAsJsonAsync(Login, new { phone, password = "s3cret-pass" }, Ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<(string UserId, string RefreshToken)> NewSessionAsync()
    {
        var (userId, phone) = await RegisterAsync();
        return (userId, (await LoginAsync(phone)).GetProperty("refreshToken").GetString()!);
    }

    private Task<HttpResponseMessage> RefreshAsync(string? token) =>
        _client.PostAsJsonAsync(Refresh, new { refreshToken = token }, Ct);

    private Task<HttpResponseMessage> LogoutAsync(string? token) =>
        _client.PostAsJsonAsync(Logout, new { refreshToken = token }, Ct);

    private static async Task<string> NextTokenAsync(HttpResponseMessage r)
    {
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("refreshToken").GetString()!;
    }

    private static async Task ShouldBeProblemAsync(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        r.StatusCode.ShouldBe(status);
        (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(code);
    }

    private async Task<RefreshToken> RowAsync(string token)
    {
        byte[] hash = Sha256Of(token);
        await using var db = factory.CreateDbContext();
        return await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenHash == hash, Ct);
    }

    private async Task<List<RefreshToken>> FamilyAsync(Guid familyId)
    {
        await using var db = factory.CreateDbContext();
        return await db.RefreshTokens.AsNoTracking().Where(t => t.FamilyId == familyId).OrderBy(t => t.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task Login_returns_an_opaque_256_bit_refresh_token_that_is_stored_only_as_its_sha256()
    {
        var (userId, phone) = await RegisterAsync();
        JsonElement body = await LoginAsync(phone);
        string token = body.GetProperty("refreshToken").GetString()!;

        token.Length.ShouldBe(43); // base64url of 32 bytes, no padding
        Base64Url.DecodeFromChars(token).Length.ShouldBe(32);
        token.ShouldNotContain('.'); // opaque, not a JWT

        RefreshToken row = await RowAsync(token);
        row.UserId.ShouldBe(Guid.Parse(userId));
        row.FamilyId.ShouldBe(row.Id); // the first token of a login is the family root
        row.TokenHash.ShouldBe(Sha256Of(token));
        (row.ExpiresAt - row.CreatedAt).ShouldBe(TimeSpan.FromDays(14));
        row.UsedAt.ShouldBeNull();
        row.RevokedAt.ShouldBeNull();
        body.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset().ShouldBe(row.ExpiresAt, TimeSpan.FromMilliseconds(1));

        // The token text appears in no column of the table, in any encoding a careless writer might use.
        await using var db = factory.CreateDbContext();
        var hits = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM refresh_tokens r
             WHERE position({token} IN r::text) > 0
                OR encode(r.token_hash, 'escape') = {token}
                OR r.token_hash = convert_to({token}, 'UTF8')
            """).SingleAsync(Ct);
        hits.ShouldBe(0);
    }

    [Fact]
    public async Task The_database_refuses_a_raw_token_where_a_hash_belongs()
    {
        var (userId, _) = await RegisterAsync();
        await using var db = factory.CreateDbContext();
        var id = Guid.CreateVersion7();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = id,
            UserId = Guid.Parse(userId),
            FamilyId = id,
            TokenHash = System.Text.Encoding.UTF8.GetBytes(RandomToken()), // 43 bytes: the token itself
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        });
        var ex = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        ex.InnerException!.Message.ShouldContain("ck_refresh_tokens_token_hash_sha256");
    }

    [Fact]
    public async Task Refresh_rotates_new_access_and_refresh_token_in_the_same_family_and_marks_the_old_one_used()
    {
        var (userId, first) = await NewSessionAsync();

        var response = await RefreshAsync(first);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("tokenType").GetString().ShouldBe("Bearer");
        body.GetProperty("expiresIn").GetInt32().ShouldBe(900);
        new JsonWebToken(body.GetProperty("accessToken").GetString()).Subject.ShouldBe(userId);
        string second = body.GetProperty("refreshToken").GetString()!;
        second.ShouldNotBe(first);

        RefreshToken old = await RowAsync(first), next = await RowAsync(second);
        old.UsedAt.ShouldNotBeNull();
        old.ReplacedBy.ShouldBe(next.Id);
        old.RevokedAt.ShouldBeNull();
        next.FamilyId.ShouldBe(old.FamilyId);
        next.UsedAt.ShouldBeNull();

        // The new access token works on a protected route of this service's family: here, a second rotation.
        string third = await NextTokenAsync(await RefreshAsync(second));
        (await FamilyAsync(old.FamilyId)).Count.ShouldBe(3);
        (await RowAsync(third)).FamilyId.ShouldBe(old.FamilyId);
    }

    [Fact]
    public async Task Reusing_a_used_token_revokes_the_whole_family_including_the_newest_descendant()
    {
        var (_, t1) = await NewSessionAsync();
        string t2 = await NextTokenAsync(await RefreshAsync(t1));
        string t3 = await NextTokenAsync(await RefreshAsync(t2));
        var (_, otherSession) = await NewSessionAsync(); // someone else's family must not be touched

        // The thief (or the victim) presents t1 again: theft signal.
        await ShouldBeProblemAsync(await RefreshAsync(t1), HttpStatusCode.Unauthorized, "identity.refresh_token_reused");

        // t3 was never used, but it is in the same family: dead too.
        await ShouldBeProblemAsync(await RefreshAsync(t3), HttpStatusCode.Unauthorized, "identity.invalid_refresh_token");
        await ShouldBeProblemAsync(await RefreshAsync(t2), HttpStatusCode.Unauthorized, "identity.invalid_refresh_token");

        var family = await FamilyAsync((await RowAsync(t1)).FamilyId);
        family.Count.ShouldBe(3); // nothing new was issued by the failed calls
        family.ShouldAllBe(t => t.RevokedAt != null);

        (await RefreshAsync(otherSession)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reusing_the_middle_token_also_kills_the_family()
    {
        var (_, t1) = await NewSessionAsync();
        string t2 = await NextTokenAsync(await RefreshAsync(t1));
        string t3 = await NextTokenAsync(await RefreshAsync(t2));

        await ShouldBeProblemAsync(await RefreshAsync(t2), HttpStatusCode.Unauthorized, "identity.refresh_token_reused");
        await ShouldBeProblemAsync(await RefreshAsync(t3), HttpStatusCode.Unauthorized, "identity.invalid_refresh_token");
    }

    [Fact]
    public async Task Parallel_refreshes_with_one_token_exactly_one_wins()
    {
        var (_, token) = await NewSessionAsync();
        const int n = RefreshThrottle.AttemptsPerWindow; // all inside the per-token limit, so none is a 429

        using var start = new SemaphoreSlim(0);
        var calls = Enumerable.Range(0, n).Select(async _ =>
        {
            await start.WaitAsync(Ct);
            return await RefreshAsync(token);
        }).ToList();
        start.Release(n);
        HttpResponseMessage[] responses = await Task.WhenAll(calls);

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var losers = responses.Where(r => r.StatusCode != HttpStatusCode.OK).ToList();
        losers.Count.ShouldBe(n - 1);
        foreach (var r in losers)
        {
            r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            string code = (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString()!;
            // The first loser sees "used" (reuse → family revoked); later ones may already see "revoked".
            code.ShouldBeOneOf("identity.refresh_token_reused", "identity.invalid_refresh_token");
        }

        // Exactly one successor was ever written, and the reuse rule then ended the family (strict rotation).
        var family = await FamilyAsync((await RowAsync(token)).FamilyId);
        family.Count.ShouldBe(2);
        family.ShouldAllBe(t => t.RevokedAt != null);
        string winner = await NextTokenAsync(responses.Single(r => r.StatusCode == HttpStatusCode.OK));
        await ShouldBeProblemAsync(await RefreshAsync(winner), HttpStatusCode.Unauthorized, "identity.invalid_refresh_token");
    }

    [Fact]
    public async Task Logout_racing_a_refresh_never_leaves_a_live_token_behind()
    {
        // The family lock's job: without it, a refresh could insert a successor after logout's UPDATE took its
        // snapshot, and that successor would stay valid after "logout".
        for (int round = 0; round < 8; round++)
        {
            var (_, token) = await NewSessionAsync();
            using var start = new SemaphoreSlim(0);
            Task<HttpResponseMessage> refresh = Task.Run(async () => { await start.WaitAsync(Ct); return await RefreshAsync(token); }, Ct);
            Task<HttpResponseMessage> logout = Task.Run(async () => { await start.WaitAsync(Ct); return await LogoutAsync(token); }, Ct);
            start.Release(2);
            await Task.WhenAll(refresh, logout);

            logout.Result.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            var family = await FamilyAsync((await RowAsync(token)).FamilyId);
            family.ShouldAllBe(t => t.RevokedAt != null);
            if (refresh.Result.StatusCode == HttpStatusCode.OK)
            {
                string next = await NextTokenAsync(refresh.Result);
                (await RefreshAsync(next)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }
            else
            {
                await ShouldBeProblemAsync(refresh.Result, HttpStatusCode.Unauthorized, "identity.invalid_refresh_token");
            }
        }
    }

    [Fact]
    public async Task An_expired_token_is_401_expired_and_issues_nothing()
    {
        var (_, token) = await NewSessionAsync();
        Guid id = (await RowAsync(token)).Id;
        await using (var db = factory.CreateDbContext())
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlAsync(
                $"UPDATE refresh_tokens SET created_at = {now.AddDays(-15)}, expires_at = {now.AddSeconds(-1)} WHERE id = {id}", Ct);
        }

        await ShouldBeProblemAsync(await RefreshAsync(token), HttpStatusCode.Unauthorized, "identity.refresh_token_expired");
        RefreshToken row = await RowAsync(token);
        row.UsedAt.ShouldBeNull();
        (await FamilyAsync(row.FamilyId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_used_token_that_has_since_expired_still_counts_as_reuse()
    {
        var (_, t1) = await NewSessionAsync();
        string t2 = await NextTokenAsync(await RefreshAsync(t1));
        Guid id = (await RowAsync(t1)).Id;
        await using (var db = factory.CreateDbContext())
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlAsync(
                $"UPDATE refresh_tokens SET created_at = {now.AddDays(-15)}, expires_at = {now.AddDays(-1)} WHERE id = {id}", Ct);
        }

        await ShouldBeProblemAsync(await RefreshAsync(t1), HttpStatusCode.Unauthorized, "identity.refresh_token_reused");
        await ShouldBeProblemAsync(await RefreshAsync(t2), HttpStatusCode.Unauthorized, "identity.invalid_refresh_token");
    }

    [Fact]
    public async Task Logout_revokes_the_family_is_idempotent_and_leaves_other_logins_alone()
    {
        var (_, phone) = await RegisterAsync();
        string phoneSession = (await LoginAsync(phone)).GetProperty("refreshToken").GetString()!;
        JsonElement laptop = await LoginAsync(phone);
        string laptopSession = laptop.GetProperty("refreshToken").GetString()!;
        string newerPhone = await NextTokenAsync(await RefreshAsync(phoneSession));

        // Logging out with the OLD (used) token still ends that login: any token of the family will do.
        (await LogoutAsync(phoneSession)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LogoutAsync(phoneSession)).StatusCode.ShouldBe(HttpStatusCode.NoContent); // again: same answer
        await ShouldBeProblemAsync(await RefreshAsync(newerPhone), HttpStatusCode.Unauthorized, "identity.invalid_refresh_token");

        // The laptop's login is its own family.
        (await RefreshAsync(laptopSession)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_with_an_unknown_token_is_401()
    {
        await ShouldBeProblemAsync(await LogoutAsync(RandomToken()), HttpStatusCode.Unauthorized, "identity.invalid_refresh_token");
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln")] // a JWT is not a refresh token
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")] // right length, not base64url
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")] // padded: 44 chars
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]  // 42 chars
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // well formed, never issued
    public async Task Garbage_tokens_are_401_on_refresh_and_logout(string token)
    {
        await ShouldBeProblemAsync(await RefreshAsync(token), HttpStatusCode.Unauthorized, "identity.invalid_refresh_token");
        await ShouldBeProblemAsync(await LogoutAsync(token), HttpStatusCode.Unauthorized, "identity.invalid_refresh_token");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_missing_token_is_400(string? token)
    {
        await ShouldBeProblemAsync(await RefreshAsync(token), HttpStatusCode.BadRequest, "identity.invalid_request");
        await ShouldBeProblemAsync(await LogoutAsync(token), HttpStatusCode.BadRequest, "identity.invalid_request");
    }

    [Fact]
    public async Task A_token_hammered_past_the_limit_gets_429_with_retry_after()
    {
        string token = RandomToken();
        for (int i = 0; i < RefreshThrottle.AttemptsPerWindow; i++)
        {
            // Refresh and logout share the counter.
            var r = i % 2 == 0 ? await RefreshAsync(token) : await LogoutAsync(token);
            r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var blocked = await RefreshAsync(token);
        await ShouldBeProblemAsync(blocked, HttpStatusCode.TooManyRequests, "identity.too_many_attempts");
        blocked.Headers.RetryAfter.ShouldNotBeNull();

        // Another token is not affected.
        (await RefreshAsync((await NewSessionAsync()).RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deleting_a_user_deletes_every_token_of_every_family()
    {
        var (userId, token) = await NewSessionAsync();
        await NextTokenAsync(await RefreshAsync(token)); // a family of 2: root + child point at each other
        Guid id = Guid.Parse(userId);

        await using var db = factory.CreateDbContext();
        await db.Database.ExecuteSqlAsync($"DELETE FROM users WHERE id = {id}", Ct);
        (await db.RefreshTokens.CountAsync(t => t.UserId == id, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task The_access_token_from_a_refresh_is_sent_as_a_bearer_token()
    {
        // Same shape as login, so a client can treat both answers the same way.
        var (_, token) = await NewSessionAsync();
        var body = await (await RefreshAsync(token)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var header = new AuthenticationHeaderValue(body.GetProperty("tokenType").GetString()!, body.GetProperty("accessToken").GetString());
        header.Scheme.ShouldBe("Bearer");
        body.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        body.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(13));
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-1.00:00:00")]
    [InlineData("00:10:00")] // shorter than the 15-minute access token
    [InlineData("91.00:00:00")]
    public async Task A_bad_refresh_lifetime_stops_identity_at_startup(string lifetime)
    {
        await using var bad = factory.WithWebHostBuilder(b => b.UseSetting("Jwt:RefreshTokenLifetime", lifetime));
        var ex = Should.Throw<OptionsValidationException>(() => bad.Server);
        ex.Message.ShouldContain("Jwt:RefreshTokenLifetime");
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("00:15:00", "14.00:00:00", true)]
    [InlineData("00:15:00", "90.00:00:00", true)]
    [InlineData("00:00:00", "14.00:00:00", false)]
    [InlineData("02:00:00", "14.00:00:00", false)]
    [InlineData("00:15:00", "00:15:00", false)]
    public void Jwt_options_validation(string access, string refresh, bool valid)
    {
        new JwtOptions { AccessTokenLifetime = TimeSpan.Parse(access, System.Globalization.CultureInfo.InvariantCulture), RefreshTokenLifetime = TimeSpan.Parse(refresh, System.Globalization.CultureInfo.InvariantCulture) }
            .IsValid().ShouldBe(valid);
    }
}
