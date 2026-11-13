using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TransitPass.Core.Api.Credentials;
using TransitPass.Core.Api.Gates;
using TransitPass.Core.Api.Ledger;
using TransitPass.Core.Api.Tickets;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Tests;

/// <summary>
/// Rafi taps his QR at Uttara North (entry) and again at Motijheel (exit). Gates are machines with their own key;
/// the QR proves the ticket. Every way it can go wrong is here: twin gates, resent taps, a longer trip he can't
/// pay for, a cancel racing the entry, forged and expired QR codes, and a storm of taps that must not deadlock.
/// </summary>
public sealed class GateTapTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Tickets = "/api/v1/tickets";
    private const long Fare = 100_00; // FakeNetwork's default fare
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private CoreFactory _factory = null!;
    private HttpClient _gate = null!; // no user token: gates authenticate with X-Gate-Key

    public ValueTask InitializeAsync()
    {
        _factory = new CoreFactory(db);
        _gate = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await _factory.DisposeAsync();
    }

    // ---------- helpers ----------

    private async Task<(Guid UserId, HttpClient Client)> UserWith(long poisha)
    {
        Guid userId = Guid.CreateVersion7();
        await using var ctx = db.CreateDbContext();
        _ = await WalletEndpoints.GetOrCreateAsync(ctx, userId, DateTimeOffset.UtcNow, Ct);
        await TopUp(userId, poisha);
        return (userId, _factory.ClientFor(userId));
    }

    private async Task TopUp(Guid userId, long poisha)
    {
        await using var ctx = db.CreateDbContext();
        Guid walletId = await ctx.Wallets.Where(w => w.UserId == userId).Select(w => w.Id).SingleAsync(Ct);
        (await LedgerBook.PostAsync(ctx, walletId, poisha, LedgerEntryType.TopUp, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Ct))
            .IsSuccess.ShouldBeTrue();
    }

    private static async Task<TicketResponse> Buy(HttpClient client, string from = "uttara-north", string to = "motijheel")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Tickets)
        {
            Content = JsonContent.Create(new { from, to }),
        };
        request.Headers.Add(TicketEndpoints.IdempotencyHeader, Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<TicketResponse>(Ct))!;
    }

    private static async Task<string> Qr(HttpClient client, Guid ticketId)
    {
        var response = await client.GetAsync(new Uri($"{Tickets}/{ticketId}/credential", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<CredentialResponse>(Ct))!.Credential;
    }

    /// <summary>A user with money, one fresh ticket and its QR.</summary>
    private async Task<(Guid UserId, HttpClient Client, TicketResponse Ticket, string Qr)> Rafi(
        long poisha = 500_00, string from = "uttara-north", string to = "motijheel")
    {
        var (userId, client) = await UserWith(poisha);
        TicketResponse ticket = await Buy(client, from, to);
        return (userId, client, ticket, await Qr(client, ticket.Id));
    }

    private async Task<HttpResponseMessage> Tap(
        string station, string direction, string credential, Guid? tapId = null, string? key = null,
        string? gateId = null, string? stationId = null)
    {
        gateId ??= $"gate-{station}";
        using var request = new HttpRequestMessage(HttpMethod.Post, GateEndpoints.TapsPath)
        {
            Content = JsonContent.Create(new
            {
                tapId = tapId ?? Guid.CreateVersion7(),
                gateId,
                stationId = stationId ?? station,
                direction,
                credential,
            }),
        };
        request.Headers.Add(GateOptions.KeyHeader, key ?? _factory.GateKeys.GetValueOrDefault(gateId, "no-key"));
        return await _gate.SendAsync(request, Ct);
    }

    // Read as a string (the content is buffered once), so a test may look at the same body more than once.
    private static async Task<JsonElement> Body(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync(Ct));

    private static async Task<string?> CodeOf(HttpResponseMessage r) => (await Body(r)).GetProperty("code").GetString();

    private static async Task Expect(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        r.StatusCode.ShouldBe(status, await r.Content.ReadAsStringAsync(Ct));
        (await CodeOf(r)).ShouldBe(code);
    }

    /// <summary>Balance (checked against SUM(ledger)) and the surcharge rows of this wallet, e.g. "-4000" ("" for none).</summary>
    private async Task<(long Balance, string Surcharges)> Books(Guid userId)
    {
        await using var ctx = db.CreateDbContext();
        var wallet = await ctx.Wallets.SingleAsync(w => w.UserId == userId, Ct);
        long sum = await ctx.LedgerEntries.Where(e => e.WalletId == wallet.Id).SumAsync(e => e.AmountPoisha, Ct);
        sum.ShouldBe(wallet.BalancePoisha);
        long[] surcharges = await ctx.LedgerEntries
            .Where(e => e.WalletId == wallet.Id && e.Type == LedgerEntryType.Surcharge)
            .Select(e => e.AmountPoisha).ToArrayAsync(Ct);
        return (wallet.BalancePoisha, string.Join(',', surcharges));
    }

    private async Task<string> StatusOf(Guid ticketId)
    {
        await using var ctx = db.CreateDbContext();
        return (await ctx.Tickets.SingleAsync(t => t.Id == ticketId, Ct)).Status.ToString();
    }

    private async Task<List<Journey>> JourneysOf(Guid ticketId)
    {
        await using var ctx = db.CreateDbContext();
        return await ctx.Journeys.AsNoTracking().Where(j => j.TicketId == ticketId).ToListAsync(Ct);
    }

    private async Task<List<GateTap>> TapsOf(Guid ticketId)
    {
        await using var ctx = db.CreateDbContext();
        return await ctx.GateTaps.AsNoTracking().Where(t => t.TicketId == ticketId).OrderBy(t => t.CreatedAt).ToListAsync(Ct);
    }

    /// <summary>Moves a ticket's service day into the past (the row and the QR signed for it).</summary>
    private async Task<string> ExpireAndSign(TicketResponse t, DateTimeOffset expiresAt)
    {
        await using (var ctx = db.CreateDbContext())
        {
            DateTimeOffset purchased = expiresAt.AddHours(-12);
            await ctx.Database.ExecuteSqlAsync(
                $"UPDATE tickets SET purchased_at = {purchased}, expires_at = {expiresAt} WHERE id = {t.Id}", Ct);
        }

        await using var read = db.CreateDbContext();
        Ticket ticket = await read.Tickets.AsNoTracking().SingleAsync(x => x.Id == t.Id, Ct);
        return _factory.Services.GetRequiredService<TicketCredentialIssuer>().Issue(ticket, ticket.PurchasedAt);
    }

    // ---------- the happy path ----------

    [Fact]
    public async Task Entry_then_exit_at_the_bought_destination_opens_both_gates_and_charges_nothing()
    {
        var (userId, _, ticket, qr) = await Rafi();
        Guid entryTap = Guid.CreateVersion7(), exitTap = Guid.CreateVersion7();

        var entry = await Tap("uttara-north", "entry", qr, entryTap);

        entry.StatusCode.ShouldBe(HttpStatusCode.OK);
        var opened = (await entry.Content.ReadFromJsonAsync<GateTapResponse>(Ct))!;
        opened.ShouldBe(new GateTapResponse(entryTap, true, "gate.entered", opened.Message, "entry", ticket.Id,
            "uttara-north", "InJourney", 0));
        (await StatusOf(ticket.Id)).ShouldBe("InJourney");
        var journey = (await JourneysOf(ticket.Id)).ShouldHaveSingleItem();
        journey.Status.ShouldBe(JourneyStatus.InProgress);
        journey.EntryStation.ShouldBe("uttara-north");
        journey.EntryTapId.ShouldBe(entryTap);
        journey.ExitStation.ShouldBeNull();

        var exit = await Tap("motijheel", "exit", qr, exitTap);

        exit.StatusCode.ShouldBe(HttpStatusCode.OK);
        var left = (await exit.Content.ReadFromJsonAsync<GateTapResponse>(Ct))!;
        (left.Allowed, left.Code, left.Direction, left.TicketStatus, left.SurchargePoisha).ShouldBe((true, "gate.exited", "exit", "Used", 0L));
        (await StatusOf(ticket.Id)).ShouldBe("Used");
        journey = (await JourneysOf(ticket.Id)).ShouldHaveSingleItem();
        (journey.Status, journey.ExitStation, journey.ExitTapId, journey.SurchargePoisha).ShouldBe(
            (JourneyStatus.Completed, "motijheel", exitTap, 0L));
        journey.ExitedAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(journey.EnteredAt);
        (await Books(userId)).ShouldBe((400_00, ""));

        var taps = await TapsOf(ticket.Id);
        taps.Select(t => (t.TapId, t.GateId, t.Direction, t.Allowed, t.Result)).ShouldBe(
        [
            (entryTap, "gate-uttara-north", GateDirection.Entry, true, "gate.entered"),
            (exitTap, "gate-motijheel", GateDirection.Exit, true, "gate.exited"),
        ]);
    }

    [Fact]
    public async Task A_longer_trip_charges_the_difference_once_as_a_surcharge_row()
    {
        _factory.Network.Fares[("uttara-north", "karwan-bazar")] = 60_00;
        _factory.Network.Fares[("uttara-north", "motijheel")] = 100_00;
        var (userId, client, ticket, qr) = await Rafi(to: "karwan-bazar");
        (await Books(userId)).Balance.ShouldBe(440_00);

        (await Tap("uttara-north", "entry", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var exit = await Tap("motijheel", "exit", qr); // he stayed on two more stops

        exit.StatusCode.ShouldBe(HttpStatusCode.OK);
        var left = (await exit.Content.ReadFromJsonAsync<GateTapResponse>(Ct))!;
        left.SurchargePoisha.ShouldBe(40_00);
        left.TicketStatus.ShouldBe("Used");
        (await Books(userId)).ShouldBe((400_00, "-4000"));
        (await JourneysOf(ticket.Id)).ShouldHaveSingleItem().SurchargePoisha.ShouldBe(40_00);

        var history = await client.GetFromJsonAsync<JsonElement>("/api/v1/wallet/me/transactions", Ct);
        var newest = history.GetProperty("items")[0];
        newest.GetProperty("type").GetString().ShouldBe("Surcharge");
        newest.GetProperty("amountPoisha").GetInt64().ShouldBe(-40_00);
        newest.GetProperty("refId").GetGuid().ShouldBe(ticket.Id);
    }

    [Fact]
    public async Task A_shorter_trip_charges_nothing_and_refunds_nothing()
    {
        _factory.Network.Fares[("uttara-north", "agargaon")] = 30_00;
        var (userId, _, _, qr) = await Rafi();

        (await Tap("uttara-north", "entry", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var exit = await Tap("agargaon", "exit", qr);

        exit.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body(exit)).GetProperty("surchargePoisha").GetInt64().ShouldBe(0);
        (await Books(userId)).ShouldBe((400_00, ""));
    }

    [Fact]
    public async Task Entry_is_allowed_at_any_station_and_the_exit_prices_the_trip_actually_made()
    {
        // Bought agargaon → karwan-bazar (৳40) but entered at uttara-north and left at motijheel (৳100).
        _factory.Network.Fares[("agargaon", "karwan-bazar")] = 40_00;
        _factory.Network.Fares[("uttara-north", "motijheel")] = 100_00;
        var (userId, _, ticket, qr) = await Rafi(from: "agargaon", to: "karwan-bazar");

        var entry = await Tap("uttara-north", "entry", qr);
        entry.StatusCode.ShouldBe(HttpStatusCode.OK);
        var exit = await Tap("motijheel", "exit", qr);

        exit.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body(exit)).GetProperty("surchargePoisha").GetInt64().ShouldBe(60_00);
        (await Books(userId)).ShouldBe((400_00, "-6000"));
        (await JourneysOf(ticket.Id)).ShouldHaveSingleItem().EntryStation.ShouldBe("uttara-north");
    }

    [Fact]
    public async Task Leaving_where_he_came_in_costs_nothing_and_asks_network_for_no_fare()
    {
        var (userId, _, _, qr) = await Rafi();
        (await Tap("agargaon", "entry", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);
        int calls = _factory.Network.Calls;

        var exit = await Tap("agargaon", "exit", qr);

        exit.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body(exit)).GetProperty("surchargePoisha").GetInt64().ShouldBe(0);
        _factory.Network.Calls.ShouldBe(calls);
        (await Books(userId)).Balance.ShouldBe(400_00);
    }

    // ---------- money at the exit ----------

    [Fact]
    public async Task A_surcharge_the_wallet_cannot_cover_is_422_and_the_ticket_stays_inside_until_a_top_up()
    {
        _factory.Network.Fares[("uttara-north", "karwan-bazar")] = 60_00;
        _factory.Network.Fares[("uttara-north", "motijheel")] = 100_00;
        var (userId, _, ticket, qr) = await Rafi(poisha: 80_00, to: "karwan-bazar"); // 20_00 left after buying
        (await Tap("uttara-north", "entry", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await Tap("motijheel", "exit", qr);

        await Expect(refused, HttpStatusCode.UnprocessableEntity, "gate.insufficient_funds");
        var problem = await Body(refused);
        problem.GetProperty("allowed").GetBoolean().ShouldBeFalse();
        problem.GetProperty("surchargePoisha").GetInt64().ShouldBe(40_00);
        problem.GetProperty("ticketStatus").GetString().ShouldBe("InJourney");
        problem.TryGetProperty("traceId", out _).ShouldBeTrue();
        (await StatusOf(ticket.Id)).ShouldBe("InJourney");
        (await JourneysOf(ticket.Id)).ShouldHaveSingleItem().Status.ShouldBe(JourneyStatus.InProgress);
        (await Books(userId)).ShouldBe((20_00, ""));
        (await TapsOf(ticket.Id)).Last().Result.ShouldBe("gate.insufficient_funds"); // the refusal is on record

        await TopUp(userId, 50_00);
        var paid = await Tap("motijheel", "exit", qr); // a new tap: the gate makes a new tap id

        paid.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Books(userId)).ShouldBe((30_00, "-4000"));
        (await StatusOf(ticket.Id)).ShouldBe("Used");
    }

    [Fact]
    public async Task A_surcharge_of_exactly_the_balance_is_paid_and_leaves_zero()
    {
        _factory.Network.Fares[("uttara-north", "karwan-bazar")] = 60_00;
        _factory.Network.Fares[("uttara-north", "motijheel")] = 100_00;
        var (userId, _, _, qr) = await Rafi(poisha: 100_00, to: "karwan-bazar"); // 40_00 left = the surcharge
        (await Tap("uttara-north", "entry", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Tap("motijheel", "exit", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Books(userId)).ShouldBe((0, "-4000"));
    }

    // ---------- resent taps (idempotency) ----------

    [Fact]
    public async Task Resending_a_tap_id_replays_the_same_answer_and_changes_nothing_twice()
    {
        _factory.Network.Fares[("uttara-north", "karwan-bazar")] = 60_00;
        _factory.Network.Fares[("uttara-north", "motijheel")] = 100_00;
        var (userId, _, ticket, qr) = await Rafi(to: "karwan-bazar");
        Guid entryTap = Guid.CreateVersion7(), exitTap = Guid.CreateVersion7();

        var first = await Tap("uttara-north", "entry", qr, entryTap);
        var again = await Tap("uttara-north", "entry", qr, entryTap);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.Headers.GetValues(TicketEndpoints.ReplayedHeader).ShouldBe(["true"]);
        first.Headers.Contains(TicketEndpoints.ReplayedHeader).ShouldBeFalse();
        (await again.Content.ReadFromJsonAsync<GateTapResponse>(Ct)).ShouldBe(await first.Content.ReadFromJsonAsync<GateTapResponse>(Ct));

        var exit = await Tap("motijheel", "exit", qr, exitTap);
        var exitAgain = await Tap("motijheel", "exit", qr, exitTap);

        exitAgain.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await exitAgain.Content.ReadFromJsonAsync<GateTapResponse>(Ct)).ShouldBe(await exit.Content.ReadFromJsonAsync<GateTapResponse>(Ct));
        (await Books(userId)).ShouldBe((400_00, "-4000")); // charged once
        (await JourneysOf(ticket.Id)).Count.ShouldBe(1);
        (await TapsOf(ticket.Id)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_refusal_is_replayed_too_with_its_status_code()
    {
        var (_, _, _, qr) = await Rafi();
        Guid tap = Guid.CreateVersion7();

        var first = await Tap("motijheel", "exit", qr, tap); // never entered
        var again = await Tap("motijheel", "exit", qr, tap);

        await Expect(first, HttpStatusCode.Conflict, "gate.not_inside");
        await Expect(again, HttpStatusCode.Conflict, "gate.not_inside");
        again.Headers.GetValues(TicketEndpoints.ReplayedHeader).ShouldBe(["true"]);
        (await Body(again)).GetProperty("tapId").GetGuid().ShouldBe(tap);
    }

    [Fact]
    public async Task A_tap_id_reused_for_a_different_tap_is_refused()
    {
        var (_, _, ticket, qr) = await Rafi();
        var (_, _, _, otherQr) = await Rafi();
        Guid tap = Guid.CreateVersion7();
        (await Tap("uttara-north", "entry", qr, tap)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await Expect(await Tap("uttara-north", "entry", otherQr, tap), HttpStatusCode.UnprocessableEntity, "gate.tap_reused");
        await Expect(await Tap("agargaon", "entry", qr, tap), HttpStatusCode.UnprocessableEntity, "gate.tap_reused");
        await Expect(await Tap("uttara-north", "exit", qr, tap), HttpStatusCode.UnprocessableEntity, "gate.tap_reused");
        (await TapsOf(ticket.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_same_tap_id_sent_20_times_at_once_enters_once_and_every_answer_is_the_same()
    {
        var (_, _, ticket, qr) = await Rafi();
        Guid tap = Guid.CreateVersion7();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Tap("uttara-north", "entry", qr, tap)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<GateTapResponse>(Ct)));
        bodies.Distinct().ShouldHaveSingleItem()!.Code.ShouldBe("gate.entered");
        responses.Count(r => !r.Headers.Contains(TicketEndpoints.ReplayedHeader)).ShouldBe(1);
        (await JourneysOf(ticket.Id)).Count.ShouldBe(1);
        (await TapsOf(ticket.Id)).Count.ShouldBe(1);
    }

    // ---------- the copied QR: twin gates ----------

    [Fact]
    public async Task The_same_QR_at_four_gates_at_once_enters_exactly_once()
    {
        var (_, _, ticket, qr) = await Rafi();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Tap(CoreFactory.GateStations[i % 4], "entry", qr)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var losers = responses.Where(r => r.StatusCode != HttpStatusCode.OK).ToList();
        losers.Count.ShouldBe(19);
        foreach (var r in losers)
        {
            await Expect(r, HttpStatusCode.Conflict, "gate.already_inside");
        }

        (await JourneysOf(ticket.Id)).Count.ShouldBe(1);
        (await TapsOf(ticket.Id)).Count(t => t.Allowed).ShouldBe(1);
        (await StatusOf(ticket.Id)).ShouldBe("InJourney");
    }

    [Fact]
    public async Task Two_exits_at_once_let_one_out_and_charge_the_surcharge_once()
    {
        _factory.Network.Fares[("uttara-north", "karwan-bazar")] = 60_00;
        _factory.Network.Fares[("uttara-north", "motijheel")] = 100_00;
        _factory.Network.Fares[("uttara-north", "agargaon")] = 100_00;
        var (userId, _, ticket, qr) = await Rafi(to: "karwan-bazar");
        (await Tap("uttara-north", "entry", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Tap(i % 2 == 0 ? "motijheel" : "agargaon", "exit", qr)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        foreach (var r in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            await Expect(r, HttpStatusCode.Conflict, "gate.already_exited");
        }

        (await Books(userId)).ShouldBe((400_00, "-4000"));
        (await StatusOf(ticket.Id)).ShouldBe("Used");
    }

    [Fact]
    public async Task An_entry_racing_a_cancel_has_exactly_one_winner_per_ticket()
    {
        var (userId, client) = await UserWith(30 * Fare);
        var tickets = new List<(TicketResponse Ticket, string Qr)>();
        for (int i = 0; i < 30; i++)
        {
            TicketResponse t = await Buy(client);
            tickets.Add((t, await Qr(client, t.Id)));
        }

        var races = tickets.Select(async t =>
        {
            var entry = Tap("uttara-north", "entry", t.Qr);
            var cancel = client.PostAsync($"{Tickets}/{t.Ticket.Id}/cancel", null, Ct);
            return (Entry: await entry, Cancel: await cancel);
        });
        var results = await Task.WhenAll(races);

        int entered = 0, cancelled = 0;
        foreach (var (entry, cancel) in results)
        {
            bool e = entry.StatusCode == HttpStatusCode.OK, c = cancel.StatusCode == HttpStatusCode.OK;
            (e ^ c).ShouldBeTrue("exactly one of entry and cancel wins");
            if (e)
            {
                entered++;
                await Expect(cancel, HttpStatusCode.Conflict, "ticket.not_cancellable");
            }
            else
            {
                cancelled++;
                await Expect(entry, HttpStatusCode.Conflict, "ticket.not_usable");
            }
        }

        await using var ctx = db.CreateDbContext();
        Guid walletId = await ctx.Wallets.Where(w => w.UserId == userId).Select(w => w.Id).SingleAsync(Ct);
        (await ctx.LedgerEntries.CountAsync(e => e.WalletId == walletId && e.Type == LedgerEntryType.Refund, Ct)).ShouldBe(cancelled);
        var ids = tickets.Select(t => t.Ticket.Id).ToList();
        (await ctx.Journeys.CountAsync(j => ids.Contains(j.TicketId), Ct)).ShouldBe(entered);
        (await Books(userId)).Balance.ShouldBe(cancelled * Fare);
    }

    // ---------- wrong state ----------

    [Fact]
    public async Task Entering_twice_and_exiting_twice_are_409()
    {
        var (_, _, _, qr) = await Rafi();
        (await Tap("uttara-north", "entry", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var twice = await Tap("uttara-north", "entry", qr);
        await Expect(twice, HttpStatusCode.Conflict, "gate.already_inside");
        (await Body(twice)).GetProperty("ticketStatus").GetString().ShouldBe("InJourney");

        (await Tap("motijheel", "exit", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Expect(await Tap("motijheel", "exit", qr), HttpStatusCode.Conflict, "gate.already_exited");
        await Expect(await Tap("uttara-north", "entry", qr), HttpStatusCode.Conflict, "ticket.not_usable"); // used: no second ride
    }

    [Fact]
    public async Task A_ticket_put_in_journey_by_hand_without_an_entry_is_refused_not_a_crash()
    {
        var (_, _, ticket, qr) = await Rafi();
        await using (var ctx = db.CreateDbContext())
        {
            await ctx.Database.ExecuteSqlAsync($"UPDATE tickets SET status = 'InJourney' WHERE id = {ticket.Id}", Ct);
        }

        var exit = await Tap("motijheel", "exit", qr);

        await Expect(exit, HttpStatusCode.Conflict, "gate.not_inside");
        (await StatusOf(ticket.Id)).ShouldBe("InJourney");
    }

    [Fact]
    public async Task Exiting_without_entering_is_409_not_inside()
    {
        var (_, _, ticket, qr) = await Rafi();
        await Expect(await Tap("motijheel", "exit", qr), HttpStatusCode.Conflict, "gate.not_inside");
        (await StatusOf(ticket.Id)).ShouldBe("Active");
    }

    [Fact]
    public async Task A_cancelled_ticket_cannot_enter_and_a_ticket_inside_cannot_be_cancelled()
    {
        var (userId, client, ticket, qr) = await Rafi();
        TicketResponse second = await Buy(client);
        string secondQr = await Qr(client, second.Id);

        (await client.PostAsync($"{Tickets}/{ticket.Id}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var entry = await Tap("uttara-north", "entry", qr); // the old QR still verifies: the database says no
        await Expect(entry, HttpStatusCode.Conflict, "ticket.not_usable");
        (await Body(entry)).GetProperty("ticketStatus").GetString().ShouldBe("Cancelled");
        await Expect(await Tap("motijheel", "exit", qr), HttpStatusCode.Conflict, "ticket.not_usable");

        (await Tap("uttara-north", "entry", secondQr)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var cancel = await client.PostAsync($"{Tickets}/{second.Id}/cancel", null, Ct);
        await Expect(cancel, HttpStatusCode.Conflict, "ticket.not_cancellable");
        (await Books(userId)).Balance.ShouldBe(400_00);
    }

    // ---------- the QR itself ----------

    [Theory]
    [InlineData("entry")]
    [InlineData("exit")]
    public async Task A_forged_or_garbage_QR_is_403_credential_invalid_and_writes_nothing(string direction)
    {
        var (_, _, ticket, qr) = await Rafi();
        string[] p = qr.Split('.');
        string tampered = $"{p[0]}.{p[1]}.{p[2][..^4]}AAAA";

        await Expect(await Tap("uttara-north", direction, tampered), HttpStatusCode.Forbidden, "credential.invalid");
        await Expect(await Tap("uttara-north", direction, "not-a-qr"), HttpStatusCode.Forbidden, "credential.invalid");
        // A login token is not a ticket.
        await Expect(await Tap("uttara-north", direction, _factory.Identity.Token()), HttpStatusCode.Forbidden, "credential.invalid");

        (await TapsOf(ticket.Id)).ShouldBeEmpty();
        (await StatusOf(ticket.Id)).ShouldBe("Active");
    }

    [Fact]
    public async Task An_expired_QR_cannot_enter()
    {
        var (_, _, ticket, _) = await Rafi();
        string expired = await ExpireAndSign(ticket, DateTimeOffset.UtcNow.AddHours(-1));

        await Expect(await Tap("uttara-north", "entry", expired), HttpStatusCode.Forbidden, "credential.expired");
        (await StatusOf(ticket.Id)).ShouldBe("Active");
    }

    [Fact]
    public async Task A_ticket_past_its_service_day_cannot_enter_even_inside_the_clock_skew()
    {
        // The QR is still inside its 30 s skew, but the row's expires_at has passed: the database decides.
        var (_, _, ticket, _) = await Rafi();
        string qr = await ExpireAndSign(ticket, DateTimeOffset.UtcNow.AddSeconds(-5));

        var entry = await Tap("uttara-north", "entry", qr);

        await Expect(entry, HttpStatusCode.Conflict, "ticket.expired");
        (await StatusOf(ticket.Id)).ShouldBe("Active");
    }

    [Fact]
    public async Task Someone_inside_at_midnight_can_still_leave_within_the_exit_grace()
    {
        var (_, _, ticket, qr) = await Rafi();
        (await Tap("uttara-north", "entry", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);
        string lateQr = await ExpireAndSign(ticket, DateTimeOffset.UtcNow.AddHours(-1)); // midnight was an hour ago

        var exit = await Tap("motijheel", "exit", lateQr);

        exit.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StatusOf(ticket.Id)).ShouldBe("Used");
    }

    [Fact]
    public async Task A_QR_expired_longer_than_the_exit_grace_cannot_exit()
    {
        var (_, _, ticket, qr) = await Rafi();
        (await Tap("uttara-north", "entry", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);
        string oldQr = await ExpireAndSign(ticket, DateTimeOffset.UtcNow.AddHours(-4)); // grace is 3 h

        await Expect(await Tap("motijheel", "exit", oldQr), HttpStatusCode.Forbidden, "credential.expired");
        (await StatusOf(ticket.Id)).ShouldBe("InJourney");
    }

    [Fact]
    public async Task A_genuine_QR_for_a_ticket_that_does_not_exist_is_404()
    {
        var ghost = new Ticket
        {
            Id = Guid.CreateVersion7(),
            UserId = Guid.CreateVersion7(),
            FromStation = "uttara-north",
            ToStation = "motijheel",
            FarePoisha = Fare,
            DistanceM = 16_500,
            Status = TicketStatus.Active,
            PurchasedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
        };
        string qr = _factory.Services.GetRequiredService<TicketCredentialIssuer>().Issue(ghost, DateTimeOffset.UtcNow);

        await Expect(await Tap("uttara-north", "entry", qr), HttpStatusCode.NotFound, "ticket.not_found");
        await Expect(await Tap("motijheel", "exit", qr), HttpStatusCode.NotFound, "ticket.not_found");
    }

    // ---------- the gate itself ----------

    [Fact]
    public async Task A_wrong_or_missing_gate_key_or_unknown_gate_is_401_and_writes_nothing()
    {
        var (_, _, ticket, qr) = await Rafi();
        string otherGatesKey = _factory.GateKeys["gate-motijheel"];

        await Expect(await Tap("uttara-north", "entry", qr, key: "wrong"), HttpStatusCode.Unauthorized, "gate.unauthorized");
        await Expect(await Tap("uttara-north", "entry", qr, key: otherGatesKey), HttpStatusCode.Unauthorized, "gate.unauthorized");
        await Expect(await Tap("uttara-north", "entry", qr, gateId: "gate-nowhere", key: otherGatesKey),
            HttpStatusCode.Unauthorized, "gate.unauthorized");

        using var noHeader = new HttpRequestMessage(HttpMethod.Post, GateEndpoints.TapsPath)
        {
            Content = JsonContent.Create(new { tapId = Guid.CreateVersion7(), gateId = "gate-uttara-north", stationId = "uttara-north", direction = "entry", credential = qr }),
        };
        await Expect(await _gate.SendAsync(noHeader, Ct), HttpStatusCode.Unauthorized, "gate.unauthorized");

        // A passenger's login token is not a gate key either.
        using var asUser = _factory.ClientFor(Guid.CreateVersion7());
        var userTry = await asUser.PostAsJsonAsync(GateEndpoints.TapsPath,
            new { tapId = Guid.CreateVersion7(), gateId = "gate-uttara-north", stationId = "uttara-north", direction = "entry", credential = qr }, Ct);
        await Expect(userTry, HttpStatusCode.Unauthorized, "gate.unauthorized");

        (await TapsOf(ticket.Id)).ShouldBeEmpty();
        (await StatusOf(ticket.Id)).ShouldBe("Active");
    }

    [Fact]
    public async Task A_gate_claiming_another_station_is_403()
    {
        var (_, _, ticket, qr) = await Rafi();

        // The Uttara North gate's own key, but it says it is at Motijheel (a cheaper exit, say).
        var r = await Tap("uttara-north", "entry", qr, stationId: "motijheel");

        await Expect(r, HttpStatusCode.Forbidden, "gate.wrong_station");
        (await StatusOf(ticket.Id)).ShouldBe("Active");
    }

    [Theory]
    [InlineData(null, "entry", "qr", "tapId")]
    [InlineData("00000000-0000-0000-0000-000000000000", "entry", "qr", "tapId")]
    [InlineData("new", "sideways", "qr", "direction")]
    [InlineData("new", null, "qr", "direction")]
    [InlineData("new", "entry", null, "credential")]
    [InlineData("new", "entry", "", "credential")]
    public async Task A_malformed_tap_is_400(string? tapId, string? direction, string? credential, string mentions)
    {
        object? id = tapId == "new" ? Guid.CreateVersion7() : tapId;
        using var request = new HttpRequestMessage(HttpMethod.Post, GateEndpoints.TapsPath)
        {
            Content = JsonContent.Create(new { tapId = id, gateId = "gate-uttara-north", stationId = "uttara-north", direction, credential }),
            Headers = { { GateOptions.KeyHeader, _factory.GateKeys["gate-uttara-north"] } },
        };
        var r = await _gate.SendAsync(request, Ct);

        await Expect(r, HttpStatusCode.BadRequest, "gate.invalid_request");
        (await Body(r)).GetProperty("title").GetString()!.ShouldContain(mentions);
    }

    [Fact]
    public async Task Direction_is_case_insensitive()
    {
        var (_, _, _, qr) = await Rafi();
        (await Tap("uttara-north", "ENTRY", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Tap("motijheel", "Exit", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---------- Network ----------

    [Fact]
    public async Task Entry_at_a_station_network_does_not_know_is_404_and_nothing_changes()
    {
        var (_, _, ticket, qr) = await Rafi();
        _factory.Network.UnknownStations["agargaon"] = true;

        await Expect(await Tap("agargaon", "entry", qr), HttpStatusCode.NotFound, "station.not_found");

        (await StatusOf(ticket.Id)).ShouldBe("Active");
        (await TapsOf(ticket.Id)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(NetworkMode.ServerError)]
    [InlineData(NetworkMode.ConnectionRefused)]
    [InlineData(NetworkMode.Timeout)]
    public async Task Network_down_closes_the_gate_with_503_and_the_same_tap_works_once_it_is_back(NetworkMode mode)
    {
        var (_, _, ticket, qr) = await Rafi();
        Guid entryTap = Guid.CreateVersion7(), exitTap = Guid.CreateVersion7();

        _factory.Network.Mode = mode;
        await Expect(await Tap("uttara-north", "entry", qr, entryTap), HttpStatusCode.ServiceUnavailable, "network.unavailable");
        (await StatusOf(ticket.Id)).ShouldBe("Active");

        _factory.Network.Mode = NetworkMode.Ok;
        (await Tap("uttara-north", "entry", qr, entryTap)).StatusCode.ShouldBe(HttpStatusCode.OK); // a 503 is not stored

        _factory.Network.Mode = mode;
        await Expect(await Tap("motijheel", "exit", qr, exitTap), HttpStatusCode.ServiceUnavailable, "network.unavailable");
        (await StatusOf(ticket.Id)).ShouldBe("InJourney");

        _factory.Network.Mode = NetworkMode.Ok;
        (await Tap("motijheel", "exit", qr, exitTap)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StatusOf(ticket.Id)).ShouldBe("Used");
    }

    // ---------- many taps at once: no deadlock ----------

    [Fact]
    public async Task A_storm_of_entries_exits_and_cancels_on_one_wallet_never_deadlocks_and_keeps_the_books()
    {
        // One wallet shared by everything below: every surcharge locks the same wallet row, and every ticket row
        // is fought over by twin gates and cancels. Lock order ticket → journey → wallet means no cycle can form.
        _factory.Network.Fares[("uttara-north", "karwan-bazar")] = 60_00;
        _factory.Network.Fares[("uttara-north", "motijheel")] = 100_00;
        _factory.Network.Fares[("uttara-north", "agargaon")] = 100_00;
        const int N = 15;
        var (userId, client) = await UserWith(N * 2 * 60_00 + N * 40_00);
        var inside = new List<(Guid Id, string Qr)>();
        var outside = new List<(Guid Id, string Qr)>();
        for (int i = 0; i < N; i++)
        {
            TicketResponse a = await Buy(client, to: "karwan-bazar");
            string qa = await Qr(client, a.Id);
            (await Tap("uttara-north", "entry", qa)).StatusCode.ShouldBe(HttpStatusCode.OK);
            inside.Add((a.Id, qa));
            TicketResponse b = await Buy(client, to: "karwan-bazar");
            outside.Add((b.Id, await Qr(client, b.Id)));
        }

        var work = new List<Task<HttpResponseMessage>>();
        foreach (var (_, qr) in inside)
        {
            work.Add(Tap("motijheel", "exit", qr));
            work.Add(Tap("agargaon", "exit", qr));
        }

        foreach (var (id, qr) in outside)
        {
            work.Add(Tap("uttara-north", "entry", qr));
            work.Add(Tap("agargaon", "entry", qr));
            work.Add(client.PostAsync($"{Tickets}/{id}/cancel", null, Ct));
        }

        var responses = await Task.WhenAll(work);

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        var (balance, surcharges) = await Books(userId);
        surcharges.Split(',').ShouldAllBe(x => x == "-4000");
        surcharges.Split(',').Length.ShouldBe(N); // one per exited ticket, never two
        await using var ctx = db.CreateDbContext();
        var outsideIds = outside.Select(o => o.Id).ToList();
        var states = await ctx.Tickets.Where(t => outsideIds.Contains(t.Id)).Select(t => t.Status).ToListAsync(Ct);
        int cancelled = states.Count(s => s == TicketStatus.Cancelled);
        (cancelled + states.Count(s => s == TicketStatus.InJourney)).ShouldBe(N);
        balance.ShouldBe(cancelled * 60_00);
        var insideIds = inside.Select(o => o.Id).ToList();
        (await ctx.Tickets.CountAsync(t => insideIds.Contains(t.Id) && t.Status == TicketStatus.Used, Ct)).ShouldBe(N);
    }

    // ---------- the database's own guards ----------

    [Fact]
    public async Task The_database_refuses_a_second_surcharge_a_second_journey_and_a_half_recorded_exit()
    {
        _factory.Network.Fares[("uttara-north", "karwan-bazar")] = 60_00;
        var (userId, _, ticket, qr) = await Rafi(to: "karwan-bazar");
        (await Tap("uttara-north", "entry", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Tap("motijheel", "exit", qr)).StatusCode.ShouldBe(HttpStatusCode.OK);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var ctx = db.CreateDbContext();
        Guid walletId = await ctx.Wallets.Where(w => w.UserId == userId).Select(w => w.Id).SingleAsync(Ct);

        var surcharge = await Should.ThrowAsync<DbUpdateException>(() =>
            LedgerBook.PostAsync(ctx, walletId, -1, LedgerEntryType.Surcharge, ticket.Id, now, Ct));
        ((PostgresException)surcharge.InnerException!).ConstraintName.ShouldBe("ux_ledger_entries_surcharge_ref");
        ctx.ChangeTracker.Clear();

        var journey = await Should.ThrowAsync<PostgresException>(() => ctx.Database.ExecuteSqlAsync($"""
            INSERT INTO journeys (id, ticket_id, entry_station, entry_tap_id, entered_at, status)
            VALUES ({Guid.CreateVersion7()}, {ticket.Id}, 'agargaon', {Guid.CreateVersion7()}, {now}, 'InProgress')
            """, Ct));
        journey.ConstraintName.ShouldBe("ux_journeys_ticket_id");

        var halfExit = await Should.ThrowAsync<PostgresException>(() => ctx.Database.ExecuteSqlAsync(
            $"UPDATE journeys SET exit_station = NULL WHERE ticket_id = {ticket.Id}", Ct));
        halfExit.ConstraintName.ShouldBe("ck_journeys_exit_all_or_nothing");

        var openWithExit = await Should.ThrowAsync<PostgresException>(() => ctx.Database.ExecuteSqlAsync(
            $"UPDATE journeys SET status = 'InProgress' WHERE ticket_id = {ticket.Id}", Ct));
        openWithExit.ConstraintName.ShouldBe("ck_journeys_completed_has_exit");

        var allowedRefusal = await Should.ThrowAsync<PostgresException>(() => ctx.Database.ExecuteSqlAsync(
            $"UPDATE gate_taps SET allowed = false WHERE ticket_id = {ticket.Id} AND result = 'gate.entered'", Ct));
        allowedRefusal.ConstraintName.ShouldBe("ck_gate_taps_allowed_result");

        (await Books(userId)).ShouldBe((400_00, "-4000"));
    }
}
