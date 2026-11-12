using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TransitPass.Core.Api.Ledger;
using TransitPass.Core.Api.Tickets;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Tests;

/// <summary>Rafi buys Uttara North → Motijheel for ৳100. Money and ticket change together, once per key.</summary>
public sealed class TicketPurchaseTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Tickets = "/api/v1/tickets";
    private const long Fare = 100_00; // ৳100
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private CoreFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new CoreFactory(db);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    // ---------- helpers ----------

    /// <summary>A user whose wallet already holds <paramref name="poisha"/> (credited like a top-up).</summary>
    private async Task<(Guid UserId, HttpClient Client)> UserWith(long poisha)
    {
        Guid userId = Guid.CreateVersion7();
        await using var ctx = db.CreateDbContext();
        Wallet wallet = await WalletEndpoints.GetOrCreateAsync(ctx, userId, DateTimeOffset.UtcNow, Ct);
        if (poisha > 0)
        {
            (await LedgerBook.PostAsync(ctx, wallet.Id, poisha, LedgerEntryType.TopUp, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Ct))
                .IsSuccess.ShouldBeTrue();
        }

        return (userId, _factory.ClientFor(userId));
    }

    private static async Task<HttpResponseMessage> Buy(
        HttpClient client, string? key, string from = "uttara-north", string to = "motijheel")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Tickets) { Content = JsonContent.Create(new { from, to }) };
        if (key is not null)
        {
            request.Headers.Add(TicketEndpoints.IdempotencyHeader, key);
        }

        return await client.SendAsync(request, Ct);
    }

    private static string NewKey() => Guid.NewGuid().ToString();

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    private static async Task<long> Balance(HttpClient client) =>
        (await client.GetFromJsonAsync<WalletResponse>("/api/v1/wallet/me", Ct))!.BalancePoisha;

    /// <summary>The money invariant, plus how many tickets and purchase rows the user has.</summary>
    private async Task<(long Balance, int Tickets, int Purchases)> Books(Guid userId)
    {
        await using var ctx = db.CreateDbContext();
        var wallet = await ctx.Wallets.SingleAsync(w => w.UserId == userId, Ct);
        long sum = await ctx.LedgerEntries.Where(e => e.WalletId == wallet.Id).SumAsync(e => e.AmountPoisha, Ct);
        sum.ShouldBe(wallet.BalancePoisha); // SUM(ledger) == balance, always
        int tickets = await ctx.Tickets.CountAsync(t => t.UserId == userId, Ct);
        int purchases = await ctx.LedgerEntries.CountAsync(e => e.WalletId == wallet.Id && e.Type == LedgerEntryType.Purchase, Ct);
        return (wallet.BalancePoisha, tickets, purchases);
    }

    // ---------- the happy path ----------

    [Fact]
    public async Task Buying_debits_the_fare_and_creates_an_active_ticket_in_one_go()
    {
        var (userId, client) = await UserWith(500_00);

        var response = await Buy(client, NewKey());

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var ticket = (await response.Content.ReadFromJsonAsync<TicketResponse>(Ct))!;
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/tickets/{ticket.Id}");
        response.Headers.Contains(TicketEndpoints.ReplayedHeader).ShouldBeFalse();
        ticket.From.ShouldBe("uttara-north");
        ticket.To.ShouldBe("motijheel");
        ticket.FarePoisha.ShouldBe(Fare);
        ticket.Status.ShouldBe("Active");
        ticket.ExpiresAt.ShouldBe(Ticket.ExpiryFor(ticket.PurchasedAt));
        (await Books(userId)).ShouldBe((400_00, 1, 1));

        var history = await client.GetFromJsonAsync<JsonElement>("/api/v1/wallet/me/transactions", Ct);
        history.GetProperty("items")[0].GetProperty("type").GetString().ShouldBe("Purchase");
        history.GetProperty("items")[0].GetProperty("refId").GetGuid().ShouldBe(ticket.Id);
        (await client.GetFromJsonAsync<TicketResponse>($"{Tickets}/{ticket.Id}", Ct))!.Id.ShouldBe(ticket.Id);
    }

    [Theory]
    [InlineData("2026-11-12T05:00:00Z", "2026-11-13T00:00:00+06:00")] // 11:00 in Dhaka → that midnight
    [InlineData("2026-11-12T17:59:59Z", "2026-11-13T00:00:00+06:00")] // 23:59:59 in Dhaka
    [InlineData("2026-11-12T18:00:00Z", "2026-11-14T00:00:00+06:00")] // 00:00 in Dhaka is already the next day
    public void A_ticket_is_valid_until_the_next_midnight_in_Dhaka(string now, string expected) =>
        Ticket.ExpiryFor(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    // ---------- idempotency ----------

    [Fact]
    public async Task A_retry_with_the_same_key_replays_the_answer_and_charges_once()
    {
        var (userId, client) = await UserWith(500_00);
        string key = NewKey();

        var first = await Buy(client, key);
        var retry = await Buy(client, key);

        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.GetValues(TicketEndpoints.ReplayedHeader).ShouldBe(["true"]);
        (await retry.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct)); // byte for byte
        retry.Headers.Location.ShouldBe(first.Headers.Location);
        _factory.Network.Calls.ShouldBe(1); // a retry doesn't even ask for the fare again
        (await Books(userId)).ShouldBe((400_00, 1, 1));
    }

    [Fact]
    public async Task The_same_key_for_a_different_trip_is_422()
    {
        var (userId, client) = await UserWith(500_00);
        string key = NewKey();
        await Buy(client, key);

        var other = await Buy(client, key, to: "agargaon");

        other.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(other)).ShouldBe("idempotency.key_reused");
        (await Books(userId)).ShouldBe((400_00, 1, 1));
    }

    [Fact]
    public async Task Keys_belong_to_one_user_so_two_users_may_send_the_same_one()
    {
        var (rafi, rafiClient) = await UserWith(500_00);
        var (zara, zaraClient) = await UserWith(500_00);

        var a = await Buy(rafiClient, "same-key-123");
        var b = await Buy(zaraClient, "same-key-123");

        b.Headers.Contains(TicketEndpoints.ReplayedHeader).ShouldBeFalse();
        (await a.Content.ReadFromJsonAsync<TicketResponse>(Ct))!.Id
            .ShouldNotBe((await b.Content.ReadFromJsonAsync<TicketResponse>(Ct))!.Id);
        (await Books(rafi)).Tickets.ShouldBe(1);
        (await Books(zara)).Tickets.ShouldBe(1);
    }

    [Theory]
    [InlineData(null, "idempotency.key_required")]
    [InlineData("has space", "idempotency.invalid_key")]
    [InlineData("ünïcode", "idempotency.invalid_key")]
    public async Task A_missing_or_bad_key_is_400_and_nothing_happens(string? key, string code)
    {
        var (userId, client) = await UserWith(500_00);

        var response = await Buy(client, key);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe(code);
        _factory.Network.Calls.ShouldBe(0);
        (await Books(userId)).ShouldBe((500_00, 0, 0));
    }

    [Fact]
    public async Task A_key_longer_than_64_characters_is_400() =>
        (await CodeOf(await Buy((await UserWith(0)).Client, new string('k', 65)))).ShouldBe("idempotency.invalid_key");

    // ---------- money ----------

    [Fact]
    public async Task Not_enough_money_is_422_with_no_ticket_and_the_key_stays_usable()
    {
        var (userId, client) = await UserWith(50_00); // ৳50, the ticket is ৳100
        string key = NewKey();

        var poor = await Buy(client, key);
        poor.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(poor)).ShouldBe("wallet.insufficient_funds");
        (await Books(userId)).ShouldBe((50_00, 0, 0));

        // Top up ৳50 more, retry with the SAME key: the failed attempt left nothing behind, so it works.
        await using (var ctx = db.CreateDbContext())
        {
            Guid walletId = (await ctx.Wallets.SingleAsync(w => w.UserId == userId, Ct)).Id;
            await LedgerBook.PostAsync(ctx, walletId, 50_00, LedgerEntryType.TopUp, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Ct);
        }

        (await Buy(client, key)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Books(userId)).ShouldBe((0, 1, 1));
    }

    // ---------- the Network service ----------

    [Theory]
    [InlineData(NetworkMode.StationNotFound, HttpStatusCode.NotFound, "station.not_found")]
    [InlineData(NetworkMode.SameStation, HttpStatusCode.BadRequest, "fare.same_station")]
    [InlineData(NetworkMode.NoRoute, HttpStatusCode.UnprocessableEntity, "fare.no_route")]
    [InlineData(NetworkMode.ServerError, HttpStatusCode.ServiceUnavailable, "network.unavailable")]
    [InlineData(NetworkMode.ConnectionRefused, HttpStatusCode.ServiceUnavailable, "network.unavailable")]
    [InlineData(NetworkMode.Timeout, HttpStatusCode.ServiceUnavailable, "network.unavailable")]
    public async Task Network_answers_and_failures_never_move_money(NetworkMode mode, HttpStatusCode status, string code)
    {
        var (userId, client) = await UserWith(500_00);
        _factory.Network.Mode = mode;
        string key = NewKey();

        var response = await Buy(client, key);

        response.StatusCode.ShouldBe(status);
        (await CodeOf(response)).ShouldBe(code);
        (await Books(userId)).ShouldBe((500_00, 0, 0));

        _factory.Network.Mode = NetworkMode.Ok; // Network is back: the same key now buys normally
        (await Buy(client, key)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_bad_body_is_400_before_Network_is_asked()
    {
        var response = await Buy((await UserWith(500_00)).Client, NewKey(), from: " ");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe("ticket.invalid_request");
        _factory.Network.Calls.ShouldBe(0);
    }

    // ---------- access ----------

    [Fact]
    public async Task Buying_needs_a_token_and_tickets_belong_to_their_owner()
    {
        var (_, client) = await UserWith(500_00);
        var ticket = (await (await Buy(client, NewKey())).Content.ReadFromJsonAsync<TicketResponse>(Ct))!;

        (await Buy(_factory.CreateClient(), NewKey())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var stranger = await _factory.ClientFor(Guid.CreateVersion7()).GetAsync($"{Tickets}/{ticket.Id}", Ct);
        stranger.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(stranger)).ShouldBe("ticket.not_found");
    }

    // ---------- ⭐ concurrency ----------

    [Fact]
    public async Task Two_hundred_simultaneous_purchases_with_money_for_ten_sell_exactly_ten()
    {
        var (userId, client) = await UserWith(10 * Fare); // ৳1000

        var responses = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Buy(client, NewKey())));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(10);
        var refused = responses.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        refused.Count.ShouldBe(190);
        refused.ShouldAllBe(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        (await CodeOf(refused[0])).ShouldBe("wallet.insufficient_funds");
        (await Books(userId)).ShouldBe((0, 10, 10)); // never negative, every taka accounted for
    }

    [Fact]
    public async Task Fifty_simultaneous_retries_of_one_key_buy_one_ticket()
    {
        var (userId, client) = await UserWith(500_00);
        string key = NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Buy(client, key)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created);
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<TicketResponse>(Ct))!.Id));
        ids.Distinct().Count().ShouldBe(1);
        responses.Count(r => !r.Headers.Contains(TicketEndpoints.ReplayedHeader)).ShouldBe(1); // one original, 49 replays
        (await Books(userId)).ShouldBe((400_00, 1, 1));
    }
}
