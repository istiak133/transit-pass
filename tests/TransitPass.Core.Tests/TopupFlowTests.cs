using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TransitPass.Core.Api.Ledger;
using TransitPass.Core.Api.Payments;
using TransitPass.Core.Api.Topups;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Tests;

/// <summary>Rafi tops up ৳500: create the intent, then the provider's signed webhook credits it, exactly once.</summary>
public sealed class TopupFlowTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Topups = "/api/v1/wallet/topups";
    private const string Webhook = "/api/v1/payments/webhook";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private CoreFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new CoreFactory(db);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    // ---------- helpers ----------

    private async Task<(HttpClient Client, TopupResponse Topup)> NewTopup(long amount = 50_000)
    {
        var client = _factory.ClientFor(Guid.CreateVersion7());
        var response = await client.PostAsJsonAsync(Topups, new { amountPoisha = amount }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (client, (await response.Content.ReadFromJsonAsync<TopupResponse>(Ct))!);
    }

    private static string EventJson(Guid topupId, long amount, string type = "payment.succeeded", string? eventId = null) =>
        JsonSerializer.Serialize(new { eventId = eventId ?? $"evt_{Guid.NewGuid():N}", type, topupId, amountPoisha = amount });

    /// <summary>Sends a webhook signed with the factory's secret (or a custom header).</summary>
    private async Task<HttpResponseMessage> SendWebhook(string json, string? signature = null, long? timestamp = null)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        long t = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, Webhook)
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } },
        };
        request.Headers.Add(WebhookSignature.Header,
            signature ?? WebhookSignature.Sign(Encoding.UTF8.GetBytes(_factory.WebhookSecret), t, body));
        return await _factory.CreateClient().SendAsync(request, Ct);
    }

    private static async Task<string> AckOf(HttpResponseMessage r)
    {
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await r.Content.ReadFromJsonAsync<WebhookAck>(Ct))!.Result;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    private static async Task<long> Balance(HttpClient client) =>
        (await client.GetFromJsonAsync<WalletResponse>("/api/v1/wallet/me", Ct))!.BalancePoisha;

    private async Task<int> InboxRows(Guid topupId)
    {
        await using var ctx = db.CreateDbContext();
        return await ctx.PaymentEvents.CountAsync(e => e.TopupId == topupId, Ct);
    }

    // ---------- creating the intent ----------

    [Fact]
    public async Task Creating_a_topup_is_201_pending_and_moves_no_money()
    {
        var (client, topup) = await NewTopup(50_000);

        topup.Status.ShouldBe("Pending");
        topup.AmountPoisha.ShouldBe(50_000);
        topup.CompletedAt.ShouldBeNull();
        topup.CheckoutUrl.ShouldEndWith(topup.Id.ToString());
        (await Balance(client)).ShouldBe(0); // the browser saying "paid" is not proof
        (await client.GetFromJsonAsync<TopupResponse>($"{Topups}/{topup.Id}", Ct))!.Status.ShouldBe("Pending");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-5_000L)]
    [InlineData(999L)]        // under ৳10
    [InlineData(5_000_001L)]  // over ৳50,000
    public async Task Bad_amounts_are_400(long? amount)
    {
        var response = await _factory.ClientFor(Guid.CreateVersion7())
            .PostAsJsonAsync(Topups, new { amountPoisha = amount }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe("topup.invalid_amount");
    }

    [Fact]
    public async Task Topups_need_a_token_and_belong_to_their_owner()
    {
        var (_, topup) = await NewTopup();

        (await _factory.CreateClient().PostAsJsonAsync(Topups, new { amountPoisha = 50_000 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var other = await _factory.ClientFor(Guid.CreateVersion7()).GetAsync($"{Topups}/{topup.Id}", Ct);
        other.StatusCode.ShouldBe(HttpStatusCode.NotFound); // not 403: don't confirm the id exists
    }

    // ---------- the webhook ----------

    [Fact]
    public async Task A_signed_success_webhook_credits_the_wallet_once_with_a_ledger_row()
    {
        var (client, topup) = await NewTopup(50_000);

        (await AckOf(await SendWebhook(EventJson(topup.Id, 50_000)))).ShouldBe("applied");

        (await Balance(client)).ShouldBe(50_000);
        var after = await client.GetFromJsonAsync<TopupResponse>($"{Topups}/{topup.Id}", Ct);
        after!.Status.ShouldBe("Succeeded");
        after.CompletedAt.ShouldNotBeNull();
        var history = await client.GetFromJsonAsync<JsonElement>("/api/v1/wallet/me/transactions", Ct);
        var entry = history.GetProperty("items")[0];
        entry.GetProperty("type").GetString().ShouldBe("TopUp");
        entry.GetProperty("refId").GetGuid().ShouldBe(topup.Id); // −/+ money always leads back to its cause
    }

    [Fact]
    public async Task The_same_event_delivered_again_is_a_duplicate_and_credits_nothing()
    {
        var (client, topup) = await NewTopup(50_000);
        string json = EventJson(topup.Id, 50_000, eventId: "evt_retry_1");

        (await AckOf(await SendWebhook(json))).ShouldBe("applied");
        (await AckOf(await SendWebhook(json))).ShouldBe("duplicate");

        (await Balance(client)).ShouldBe(50_000);
    }

    [Fact]
    public async Task Ten_simultaneous_deliveries_of_one_event_credit_once()
    {
        var (client, topup) = await NewTopup(50_000);
        string json = EventJson(topup.Id, 50_000);

        var acks = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ => await AckOf(await SendWebhook(json))));

        acks.Count(a => a == "applied").ShouldBe(1);
        acks.Count(a => a == "duplicate").ShouldBe(9);
        (await Balance(client)).ShouldBe(50_000);
    }

    [Fact]
    public async Task Ten_different_success_events_for_one_topup_credit_once()
    {
        var (client, topup) = await NewTopup(50_000);

        var acks = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(async _ => await AckOf(await SendWebhook(EventJson(topup.Id, 50_000)))));

        acks.Count(a => a == "applied").ShouldBe(1);
        acks.Count(a => a == "already_final").ShouldBe(9); // the state machine, not the inbox, stopped these
        (await Balance(client)).ShouldBe(50_000);
    }

    [Fact]
    public async Task A_failed_payment_credits_nothing_and_a_late_success_cannot_revive_it()
    {
        var (client, topup) = await NewTopup(50_000);

        (await AckOf(await SendWebhook(EventJson(topup.Id, 50_000, "payment.failed")))).ShouldBe("applied");
        (await AckOf(await SendWebhook(EventJson(topup.Id, 50_000)))).ShouldBe("already_final");

        (await Balance(client)).ShouldBe(0);
        (await client.GetFromJsonAsync<TopupResponse>($"{Topups}/{topup.Id}", Ct))!.Status.ShouldBe("Failed");
    }

    [Fact]
    public async Task A_forged_signature_is_401_and_leaves_no_trace()
    {
        var (client, topup) = await NewTopup(50_000);
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var response = await SendWebhook(EventJson(topup.Id, 50_000), signature: $"t={t},v1={new string('a', 64)}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CodeOf(response)).ShouldBe("payment.invalid_signature");
        (await Balance(client)).ShouldBe(0);
        (await InboxRows(topup.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task A_replayed_old_webhook_is_401()
    {
        var (client, topup) = await NewTopup(50_000);

        var response = await SendWebhook(EventJson(topup.Id, 50_000),
            timestamp: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds());

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Balance(client)).ShouldBe(0);
    }

    [Fact]
    public async Task An_amount_mismatch_is_422_and_rolls_back_so_a_correct_retry_still_works()
    {
        var (client, topup) = await NewTopup(50_000);
        const string EventId = "evt_mismatch_1";

        var wrong = await SendWebhook(EventJson(topup.Id, 5_000, eventId: EventId));
        wrong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(wrong)).ShouldBe("payment.amount_mismatch");
        (await InboxRows(topup.Id)).ShouldBe(0); // the inbox row rolled back with everything else

        (await AckOf(await SendWebhook(EventJson(topup.Id, 50_000, eventId: EventId)))).ShouldBe("applied");
        (await Balance(client)).ShouldBe(50_000);
    }

    [Fact]
    public async Task An_unknown_topup_is_404()
    {
        var response = await SendWebhook(EventJson(Guid.CreateVersion7(), 50_000));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(response)).ShouldBe("payment.unknown_topup");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"type":"payment.succeeded"}""")]
    [InlineData("""{"eventId":"e1","type":"payment.refunded","topupId":"01a1cf30-0000-7000-8000-000000000001","amountPoisha":100}""")]
    public async Task A_signed_but_malformed_event_is_400(string json)
    {
        var response = await SendWebhook(json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe("payment.invalid_event");
    }

    [Fact]
    public async Task A_huge_body_is_refused_before_anything_else()
    {
        var response = await SendWebhook(new string(' ', PaymentWebhook.MaxBodyBytes + 1), signature: "t=1,v1=00");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe("payment.body_too_large");
    }

    [Fact]
    public async Task The_database_allows_only_one_topup_ledger_row_per_topup()
    {
        var (_, topup) = await NewTopup(50_000);
        await AckOf(await SendWebhook(EventJson(topup.Id, 50_000)));
        await using var ctx = db.CreateDbContext();
        Guid walletId = await ctx.Topups.Where(t => t.Id == topup.Id).Select(t => t.WalletId).SingleAsync(Ct);

        // Even code that skipped the inbox and the state machine can't credit the same top-up twice.
        var ex = await Should.ThrowAsync<DbUpdateException>(() =>
            LedgerBook.PostAsync(ctx, walletId, 50_000, LedgerEntryType.TopUp, topup.Id, DateTimeOffset.UtcNow, Ct));

        ex.InnerException.ShouldBeOfType<Npgsql.PostgresException>().ConstraintName.ShouldBe("ux_ledger_entries_topup_ref");
    }
}
