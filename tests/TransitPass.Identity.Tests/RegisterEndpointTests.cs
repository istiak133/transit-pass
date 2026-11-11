using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TransitPass.Identity.Tests;

public sealed class RegisterEndpointTests(IdentityFactory factory) : IClassFixture<IdentityFactory>
{
    private const string Url = "/api/v1/identity/users";
    private readonly HttpClient _client = factory.CreateClient();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Each test uses its own phone, so tests don't collide in the shared database.
    private static string NewPhone() => "017" + Random.Shared.Next(10_000_000, 99_999_999);

    [Fact]
    public async Task Register_returns_201_with_location_and_no_secret()
    {
        var response = await _client.PostAsJsonAsync(Url, new { phone = NewPhone(), name = "  Rafi Hasan ", password = "s3cret-pass" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        string id = body.GetProperty("id").GetString()!;
        response.Headers.Location!.ToString().ShouldBe($"{Url}/{id}");
        body.GetProperty("name").GetString().ShouldBe("Rafi Hasan"); // trimmed
        body.GetProperty("phone").GetString()!.ShouldStartWith("+8801");
        body.TryGetProperty("passwordHash", out _).ShouldBeFalse();

        // In the database: hashed, never plain.
        await using var db = factory.CreateDbContext();
        var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Id == Guid.Parse(id), Ct);
        stored.PasswordHash.ShouldNotContain("s3cret-pass");
        stored.Id.Version.ShouldBe(7);
    }

    [Fact]
    public async Task Same_phone_in_another_format_is_409()
    {
        string local = NewPhone();                 // 017XXXXXXXX
        string international = "+880" + local[1..]; // +88017XXXXXXXX

        (await _client.PostAsJsonAsync(Url, new { phone = local, name = "A", password = "password1" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var second = await _client.PostAsJsonAsync(Url, new { phone = international, name = "B", password = "password2" }, Ct);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("user.phone_taken");
    }

    [Fact]
    public async Task Concurrent_signups_with_same_phone_create_exactly_one_user()
    {
        string phone = NewPhone();
        var attempts = Enumerable.Range(0, 10).Select(i =>
            _client.PostAsJsonAsync(Url, new { phone, name = $"Racer {i}", password = "password1" }, Ct));

        var statuses = (await Task.WhenAll(attempts)).Select(r => r.StatusCode).ToList();

        statuses.Count(s => s == HttpStatusCode.Created).ShouldBe(1);
        statuses.Count(s => s == HttpStatusCode.Conflict).ShouldBe(9);

        await using var db = factory.CreateDbContext();
        string e164 = "+880" + phone[1..];
        (await db.Users.CountAsync(u => u.Phone == e164, Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData("12345", "Rafi", "password1", "user.invalid_phone")]
    [InlineData(null, "Rafi", "password1", "user.invalid_phone")]
    [InlineData("01711000009", "   ", "password1", "user.invalid_name")]
    [InlineData("01711000009", null, "password1", "user.invalid_name")]
    [InlineData("01711000009", "Rafi", "short", "user.invalid_password")]
    [InlineData("01711000009", "Rafi", null, "user.invalid_password")]
    public async Task Invalid_input_is_400_with_code(string? phone, string? name, string? password, string code)
    {
        var response = await _client.PostAsJsonAsync(Url, new { phone, name, password }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task Too_long_password_is_rejected_before_hashing()
    {
        var response = await _client.PostAsJsonAsync(Url, new { phone = NewPhone(), name = "Rafi", password = new string('x', 129) }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Database_refuses_non_e164_phone()
    {
        await using var db = factory.CreateDbContext();
        await Should.ThrowAsync<DbUpdateException>(async () =>
        {
            db.Users.Add(new TransitPass.Identity.Api.Users.User
            {
                Id = Guid.CreateVersion7(),
                Phone = "01711000001",
                Name = "x",
                PasswordHash = "x",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        });
    }
}
