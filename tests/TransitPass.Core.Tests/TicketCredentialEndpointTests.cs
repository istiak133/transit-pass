using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TransitPass.Core.Api.Credentials;
using TransitPass.Core.Api.Ledger;
using TransitPass.Core.Api.Tickets;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Tests;

/// <summary>Rafi opens his ticket and the app asks for its QR credential. Gates fetch the public keys.</summary>
public sealed class TicketCredentialEndpointTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Tickets = "/api/v1/tickets";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private CoreFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new CoreFactory(db);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    // ---------- helpers ----------

    private async Task<(Guid UserId, HttpClient Client)> UserWith(long poisha)
    {
        Guid userId = Guid.CreateVersion7();
        await using var ctx = db.CreateDbContext();
        Wallet wallet = await WalletEndpoints.GetOrCreateAsync(ctx, userId, DateTimeOffset.UtcNow, Ct);
        (await LedgerBook.PostAsync(ctx, wallet.Id, poisha, LedgerEntryType.TopUp, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Ct))
            .IsSuccess.ShouldBeTrue();
        return (userId, _factory.ClientFor(userId));
    }

    private static async Task<TicketResponse> Buy(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Tickets)
        {
            Content = JsonContent.Create(new { from = "uttara-north", to = "motijheel" }),
        };
        request.Headers.Add(TicketEndpoints.IdempotencyHeader, Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<TicketResponse>(Ct))!;
    }

    private static Task<HttpResponseMessage> Credential(HttpClient client, Guid id) =>
        client.GetAsync(new Uri($"{Tickets}/{id}/credential", UriKind.Relative), Ct);

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    private TicketCredentialVerifier Verifier => _factory.Services.GetRequiredService<TicketCredentialVerifier>();

    private async Task<(long Balance, int Entries)> Books(Guid userId)
    {
        await using var ctx = db.CreateDbContext();
        var wallet = await ctx.Wallets.SingleAsync(w => w.UserId == userId, Ct);
        long sum = await ctx.LedgerEntries.Where(e => e.WalletId == wallet.Id).SumAsync(e => e.AmountPoisha, Ct);
        sum.ShouldBe(wallet.BalancePoisha);
        return (wallet.BalancePoisha, await ctx.LedgerEntries.CountAsync(e => e.WalletId == wallet.Id, Ct));
    }

    private async Task Sql(FormattableString sql)
    {
        await using var ctx = db.CreateDbContext();
        (await ctx.Database.ExecuteSqlAsync(sql, Ct)).ShouldBe(1);
    }

    // ---------- the credential ----------

    [Fact]
    public async Task Owner_gets_a_signed_credential_that_a_gate_can_verify()
    {
        var (userId, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);

        var response = await Credential(client, ticket.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var body = (await response.Content.ReadFromJsonAsync<CredentialResponse>(Ct))!;
        body.TicketId.ShouldBe(ticket.Id);
        body.ExpiresAt.ShouldBe(ticket.ExpiresAt);

        var claims = await Verifier.VerifyAsync(body.Credential);
        claims.IsSuccess.ShouldBeTrue();
        claims.Value.TicketId.ShouldBe(ticket.Id);
        claims.Value.UserId.ShouldBe(userId);
        claims.Value.From.ShouldBe("uttara-north");
        claims.Value.To.ShouldBe("motijheel");
        claims.Value.ExpiresAt.ShouldBe(ticket.ExpiresAt); // exp is exactly the ticket's own expiry (Dhaka midnight)
    }

    [Fact]
    public async Task Asking_for_a_credential_is_read_only_and_moves_no_money()
    {
        var (userId, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);
        var before = await Books(userId);

        for (int i = 0; i < 3; i++)
        {
            (await Credential(client, ticket.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await Books(userId)).ShouldBe(before);
        (await client.GetFromJsonAsync<TicketResponse>($"{Tickets}/{ticket.Id}", Ct))!.Status.ShouldBe("Active");
    }

    [Fact]
    public async Task Someone_elses_ticket_and_an_unknown_id_are_the_same_404()
    {
        var (_, owner) = await UserWith(500_00);
        TicketResponse ticket = await Buy(owner);
        var (_, stranger) = await UserWith(1000);

        var theirs = await Credential(stranger, ticket.Id);
        var unknown = await Credential(stranger, Guid.CreateVersion7());

        theirs.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(theirs)).ShouldBe("ticket.not_found");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(unknown)).ShouldBe("ticket.not_found");
        (await theirs.Content.ReadAsStringAsync(Ct)).ShouldNotContain("credential\":");
    }

    [Fact]
    public async Task Without_a_token_it_is_401()
    {
        var (_, owner) = await UserWith(500_00);
        TicketResponse ticket = await Buy(owner);

        var response = await Credential(_factory.CreateClient(), ticket.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CodeOf(response)).ShouldBe("auth.unauthenticated");
    }

    [Fact]
    public async Task A_cancelled_ticket_has_no_credential()
    {
        var (_, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);
        (await client.PostAsync($"{Tickets}/{ticket.Id}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await Credential(client, ticket.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("ticket.not_usable");
    }

    [Theory]
    [InlineData("Used")]
    [InlineData("Expired")]
    public async Task A_used_or_expired_ticket_has_no_credential(string status)
    {
        var (_, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);
        await Sql($"UPDATE tickets SET status = {status} WHERE id = {ticket.Id}");

        var response = await Credential(client, ticket.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("ticket.not_usable");
    }

    [Fact]
    public async Task An_in_journey_ticket_still_gets_one_so_rafi_can_exit()
    {
        var (_, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);
        await Sql($"UPDATE tickets SET status = 'InJourney' WHERE id = {ticket.Id}");

        var response = await Credential(client, ticket.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_active_ticket_past_its_service_day_is_ticket_expired()
    {
        // The expiry job (Phase 13) hasn't flipped it yet, but the clock says the day is over.
        var (_, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);
        await Sql($"""
            UPDATE tickets SET purchased_at = now() - interval '2 days', expires_at = now() - interval '1 day'
             WHERE id = {ticket.Id}
            """);

        var response = await Credential(client, ticket.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("ticket.expired");
    }

    [Fact]
    public async Task A_cancelled_tickets_old_qr_still_verifies_so_the_gate_must_check_the_database()
    {
        // Signature = genuine. It cannot say "still unused": that is the ticket row's job (Phase 12 gate).
        var (_, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);
        var body = (await (await Credential(client, ticket.Id)).Content.ReadFromJsonAsync<CredentialResponse>(Ct))!;
        (await client.PostAsync($"{Tickets}/{ticket.Id}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Verifier.VerifyAsync(body.Credential)).IsSuccess.ShouldBeTrue();
        await using var ctx = db.CreateDbContext();
        (await ctx.Tickets.SingleAsync(t => t.Id == ticket.Id, Ct)).Status.ShouldBe(TicketStatus.Cancelled);
    }

    [Fact]
    public async Task Fifty_parallel_requests_all_get_a_valid_credential_for_the_same_ticket()
    {
        var (userId, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);
        var before = await Books(userId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Credential(client, ticket.Id)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        foreach (var r in responses)
        {
            var body = (await r.Content.ReadFromJsonAsync<CredentialResponse>(Ct))!;
            (await Verifier.VerifyAsync(body.Credential)).Value.TicketId.ShouldBe(ticket.Id);
        }

        (await Books(userId)).ShouldBe(before);
    }

    [Fact]
    public async Task Credential_and_cancel_racing_never_hand_out_a_qr_after_a_409_or_refund_twice()
    {
        var (userId, client) = await UserWith(1000_00);
        TicketResponse ticket = await Buy(client);

        var cancels = Enumerable.Range(0, 10).Select(_ => client.PostAsync($"{Tickets}/{ticket.Id}/cancel", null, Ct));
        var credentials = Enumerable.Range(0, 20).Select(_ => Credential(client, ticket.Id));
        var all = await Task.WhenAll(cancels.Concat(credentials));

        all.Take(10).ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK); // cancel is idempotent
        foreach (var r in all.Skip(10))
        {
            // Each credential request saw the ticket either before (200) or after (409) the cancel.
            r.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
            if (r.StatusCode == HttpStatusCode.Conflict)
            {
                (await CodeOf(r)).ShouldBe("ticket.not_usable");
            }
        }

        (await Books(userId)).ShouldBe((1000_00, 3)); // top-up, purchase, ONE refund
        (await Credential(client, ticket.Id)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ---------- tokens can't swap roles ----------

    [Fact]
    public async Task A_ticket_credential_is_not_accepted_as_a_login_token()
    {
        var (_, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);
        var body = (await (await Credential(client, ticket.Id)).Content.ReadFromJsonAsync<CredentialResponse>(Ct))!;

        using var asBearer = _factory.CreateClient();
        asBearer.DefaultRequestHeaders.Authorization = new("Bearer", body.Credential);
        var response = await asBearer.GetAsync(new Uri("/api/v1/wallet/me", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_login_token_is_not_accepted_as_a_ticket_credential()
    {
        string accessToken = _factory.Identity.Token(sub: Guid.CreateVersion7());

        var result = await Verifier.VerifyAsync(accessToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("credential.invalid");
    }

    [Fact]
    public void The_qr_key_is_not_identitys_key()
    {
        QrKeys keys = _factory.Services.GetRequiredService<QrKeys>();
        keys.Signing.KeyId.ShouldNotBe(_factory.Identity.SigningKey.KeyId);
        _factory.Services.GetRequiredService<IOptions<QrOptions>>().Value.Issuer.ShouldNotBe("transitpass-identity");
    }

    // ---------- JWKS for gates ----------

    [Fact]
    public async Task Jwks_is_public_cacheable_holds_no_private_part_and_verifies_a_real_credential()
    {
        var (_, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);
        var body = (await (await Credential(client, ticket.Id)).Content.ReadFromJsonAsync<CredentialResponse>(Ct))!;

        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(new Uri(CredentialEndpoints.JwksPath, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.Public.ShouldBeTrue();
        response.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromMinutes(5));
        string json = await response.Content.ReadAsStringAsync(Ct);
        json.ShouldNotContain("\"d\"");

        var keys = new JsonWebKeySet(json);
        keys.Keys.Single().Kid.ShouldBe(_factory.Services.GetRequiredService<QrKeys>().Signing.KeyId);
        keys.Keys.Single().Alg.ShouldBe("ES256");

        // A gate with ONLY this document (no database, no secret) can check Rafi's QR.
        var gate = new TicketCredentialVerifier(keys.GetSigningKeys(), new QrOptions(), TimeProvider.System);
        (await gate.VerifyAsync(body.Credential)).Value.TicketId.ShouldBe(ticket.Id);
    }
}
