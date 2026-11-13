using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TransitPass.Core.Api.Ledger;
using TransitPass.Core.Api.Tickets;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Tests;

/// <summary>Rafi lists his tickets and cancels one he no longer needs. The fare comes back exactly once.</summary>
public sealed class TicketLifecycleTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Tickets = "/api/v1/tickets";
    private const long Fare = 100_00;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private CoreFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new CoreFactory(db);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private sealed record Page(List<TicketResponse> Items, string? NextCursor);

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

    private static Task<HttpResponseMessage> Cancel(HttpClient client, Guid id) =>
        client.PostAsync($"{Tickets}/{id}/cancel", null, Ct);

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    /// <summary>Balance (checked against SUM(ledger)) and how many refund rows exist.</summary>
    private async Task<(long Balance, int Refunds)> Books(Guid userId)
    {
        await using var ctx = db.CreateDbContext();
        var wallet = await ctx.Wallets.SingleAsync(w => w.UserId == userId, Ct);
        long sum = await ctx.LedgerEntries.Where(e => e.WalletId == wallet.Id).SumAsync(e => e.AmountPoisha, Ct);
        sum.ShouldBe(wallet.BalancePoisha);
        int refunds = await ctx.LedgerEntries.CountAsync(e => e.WalletId == wallet.Id && e.Type == LedgerEntryType.Refund, Ct);
        return (wallet.BalancePoisha, refunds);
    }

    /// <summary>Moves a ticket the way a gate (Phase 12) or the expiry job (Phase 13) will.</summary>
    private async Task<int> SetStatus(Guid ticketId, string status)
    {
        await using var ctx = db.CreateDbContext();
        return await ctx.Database.ExecuteSqlAsync(
            $"UPDATE tickets SET status = {status} WHERE id = {ticketId} AND status = 'Active'", Ct);
    }

    // ---------- cancel ----------

    [Fact]
    public async Task Cancel_refunds_the_fare_once_and_marks_the_ticket_cancelled()
    {
        var (userId, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);
        (await Books(userId)).ShouldBe((400_00, 0));

        var response = await Cancel(client, ticket.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cancelled = (await response.Content.ReadFromJsonAsync<TicketResponse>(Ct))!;
        cancelled.Status.ShouldBe("Cancelled");
        cancelled.CancelledAt.ShouldNotBeNull();
        (await Books(userId)).ShouldBe((500_00, 1));

        var history = await client.GetFromJsonAsync<JsonElement>("/api/v1/wallet/me/transactions", Ct);
        var newest = history.GetProperty("items")[0];
        newest.GetProperty("type").GetString().ShouldBe("Refund");
        newest.GetProperty("amountPoisha").GetInt64().ShouldBe(Fare);
        newest.GetProperty("refId").GetGuid().ShouldBe(ticket.Id);
    }

    [Fact]
    public async Task Cancelling_again_returns_the_same_answer_without_a_second_refund()
    {
        var (userId, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);
        var first = (await (await Cancel(client, ticket.Id)).Content.ReadFromJsonAsync<TicketResponse>(Ct))!;

        var again = await Cancel(client, ticket.Id);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<TicketResponse>(Ct)).ShouldBe(first);
        (await Books(userId)).ShouldBe((500_00, 1));
    }

    [Fact]
    public async Task Someone_elses_ticket_or_an_unknown_id_is_404_and_moves_no_money()
    {
        var (owner, ownerClient) = await UserWith(500_00);
        TicketResponse ticket = await Buy(ownerClient);
        var (_, stranger) = await UserWith(1000);

        var theirs = await Cancel(stranger, ticket.Id);
        var unknown = await Cancel(stranger, Guid.CreateVersion7());

        theirs.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(theirs)).ShouldBe("ticket.not_found");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Books(owner)).ShouldBe((400_00, 0));
        (await ownerClient.GetFromJsonAsync<TicketResponse>($"{Tickets}/{ticket.Id}", Ct))!.Status.ShouldBe("Active");
    }

    [Theory]
    [InlineData("InJourney")]
    [InlineData("Used")]
    [InlineData("Expired")]
    public async Task A_ticket_that_left_active_cannot_be_cancelled(string status)
    {
        var (userId, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);
        (await SetStatus(ticket.Id, status)).ShouldBe(1);

        var response = await Cancel(client, ticket.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("ticket.not_cancellable");
        (await Books(userId)).ShouldBe((400_00, 0));
    }

    [Fact]
    public async Task An_active_ticket_past_its_service_day_is_409_expired()
    {
        // The expiry job (Phase 13) has not run yet, so the row still says Active. The clock decides.
        var (userId, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);
        await using (var ctx = db.CreateDbContext())
        {
            await ctx.Database.ExecuteSqlAsync($"""
                UPDATE tickets SET purchased_at = now() - interval '2 days', expires_at = now() - interval '1 day'
                 WHERE id = {ticket.Id}
                """, Ct);
        }

        var response = await Cancel(client, ticket.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("ticket.expired");
        (await Books(userId)).ShouldBe((400_00, 0));
    }

    [Fact]
    public async Task Twenty_cancels_at_once_refund_exactly_once()
    {
        var (userId, client) = await UserWith(500_00);
        TicketResponse ticket = await Buy(client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Cancel(client, ticket.Id)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK); // the losers wait, then see Cancelled
        (await Books(userId)).ShouldBe((500_00, 1));
    }

    [Fact]
    public async Task Cancel_racing_a_gate_scan_has_exactly_one_winner_per_ticket()
    {
        // The gate does what Phase 12 will: UPDATE … SET status = 'InJourney' WHERE status = 'Active'.
        var (userId, client) = await UserWith(30 * Fare);
        List<TicketResponse> tickets = [];
        for (int i = 0; i < 30; i++)
        {
            tickets.Add(await Buy(client));
        }

        var races = tickets.Select(async t =>
        {
            Task<HttpResponseMessage> cancel = Cancel(client, t.Id);
            Task<int> gate = SetStatus(t.Id, "InJourney");
            await Task.WhenAll(cancel, gate);
            return (Cancel: (await cancel).StatusCode, GateRows: await gate);
        });
        var results = await Task.WhenAll(races);

        foreach (var (cancel, gateRows) in results)
        {
            // Exactly one side moved the ticket out of Active.
            (cancel == HttpStatusCode.OK ^ gateRows == 1).ShouldBeTrue();
            if (cancel != HttpStatusCode.OK)
            {
                cancel.ShouldBe(HttpStatusCode.Conflict);
            }
        }

        int cancelled = results.Count(r => r.Cancel == HttpStatusCode.OK);
        (await Books(userId)).ShouldBe((cancelled * Fare, cancelled)); // one refund per winning cancel, none for gates
    }

    [Fact]
    public async Task The_database_refuses_a_second_refund_for_the_same_ticket()
    {
        // Defence in depth: even code that skipped the status guard could not refund twice.
        var (userId, _) = await UserWith(1000);
        await using var ctx = db.CreateDbContext();
        Guid walletId = await ctx.Wallets.Where(w => w.UserId == userId).Select(w => w.Id).SingleAsync(Ct);
        Guid ticketId = Guid.CreateVersion7();
        (await LedgerBook.PostAsync(ctx, walletId, Fare, LedgerEntryType.Refund, ticketId, DateTimeOffset.UtcNow, Ct))
            .IsSuccess.ShouldBeTrue();

        await using var ctx2 = db.CreateDbContext();
        var second = await Should.ThrowAsync<DbUpdateException>(() =>
            LedgerBook.PostAsync(ctx2, walletId, Fare, LedgerEntryType.Refund, ticketId, DateTimeOffset.UtcNow, Ct));

        second.InnerException!.Message.ShouldContain("ux_ledger_entries_refund_ref");
        (await Books(userId)).ShouldBe((1000 + Fare, 1)); // the failed posting rolled back its balance change too
    }

    // ---------- list ----------

    [Fact]
    public async Task List_pages_through_my_tickets_newest_first_and_shows_only_mine()
    {
        var (_, client) = await UserWith(500_00);
        var (_, other) = await UserWith(500_00);
        await Buy(other);
        List<Guid> bought = [];
        for (int i = 0; i < 5; i++)
        {
            bought.Add((await Buy(client)).Id);
        }

        await Cancel(client, bought[1]);

        List<TicketResponse> seen = [];
        List<int> sizes = [];
        string url = $"{Tickets}?limit=2";
        while (true)
        {
            var page = (await client.GetFromJsonAsync<Page>(url, Ct))!;
            seen.AddRange(page.Items);
            sizes.Add(page.Items.Count);
            if (page.NextCursor is null)
            {
                break;
            }

            url = $"{Tickets}?limit=2&after={page.NextCursor}";
        }

        sizes.ShouldBe([2, 2, 1]);
        seen.Select(t => t.Id).ShouldBe(Enumerable.Reverse(bought)); // newest first, nothing missed or repeated
        seen.Single(t => t.Status == "Cancelled").Id.ShouldBe(bought[1]);
    }

    [Fact]
    public async Task List_with_no_tickets_is_an_empty_page_and_a_bad_cursor_is_400()
    {
        var (_, client) = await UserWith(1000);

        var empty = (await client.GetFromJsonAsync<Page>(Tickets, Ct))!;
        var bad = await client.GetAsync($"{Tickets}?after=not-a-cursor", Ct);

        empty.Items.ShouldBeEmpty();
        empty.NextCursor.ShouldBeNull();
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_and_cancel_need_a_token()
    {
        var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync(Tickets, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Cancel(anonymous, Guid.CreateVersion7())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
