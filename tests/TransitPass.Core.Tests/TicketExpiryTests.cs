using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TransitPass.Core.Api.Jobs;
using TransitPass.Core.Api.Ledger;
using TransitPass.Core.Api.Tickets;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Tests;

/// <summary>
/// Rafi bought a ticket and never rode. After midnight the expiry job marks it Expired and gives his fare back,
/// once, however many Core instances run the job at the same moment.
/// <para>
/// Isolation: every test class shares one database, and other classes keep Active tickets that expired yesterday
/// on purpose. So each test here runs the job with a clock set in its own year of the 1990s, and its tickets
/// expire in that year: the job (which only looks at expires_at &lt;= its clock) never sees anyone else's rows.
/// Tests in one class run one after another, and each leaves no Active ticket behind in its year.
/// </para>
/// </summary>
public sealed class TicketExpiryTests(PostgresFixture db) : IAsyncLifetime
{
    private const long Fare = 100_00;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private CoreFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new CoreFactory(db);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    /// <summary>Midnight (UTC) on 1 January of <paramref name="year"/>: the "now" a test runs the job at.</summary>
    private static DateTimeOffset Era(int year) => new(year, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---------- helpers ----------

    private async Task<(Guid UserId, Guid WalletId)> UserWith(long poisha)
    {
        Guid userId = Guid.CreateVersion7();
        await using var ctx = db.CreateDbContext();
        Wallet wallet = await WalletEndpoints.GetOrCreateAsync(ctx, userId, DateTimeOffset.UtcNow, Ct);
        (await LedgerBook.PostAsync(ctx, wallet.Id, poisha, LedgerEntryType.TopUp, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Ct))
            .IsSuccess.ShouldBeTrue();
        return (userId, wallet.Id);
    }

    /// <summary>
    /// A ticket as a purchase leaves it (the fare debited through the ledger), bought a day before
    /// <paramref name="expiresAt"/>. Inserted directly so the expiry can be in any year.
    /// </summary>
    private async Task<Guid> Ticket(Guid userId, DateTimeOffset expiresAt, string status = "Active", long fare = Fare, bool pay = true)
    {
        Guid id = Guid.CreateVersion7();
        await using var ctx = db.CreateDbContext();
        await using var tx = await ctx.Database.BeginTransactionAsync(Ct);
        if (pay)
        {
            Guid walletId = await ctx.Wallets.Where(w => w.UserId == userId).Select(w => w.Id).SingleAsync(Ct);
            (await LedgerBook.PostAsync(ctx, walletId, -fare, LedgerEntryType.Purchase, id, DateTimeOffset.UtcNow, Ct))
                .IsSuccess.ShouldBeTrue();
        }

        DateTimeOffset? cancelledAt = status == "Cancelled" ? expiresAt.AddHours(-1) : null;
        await ctx.Database.ExecuteSqlAsync($"""
            INSERT INTO tickets (id, user_id, from_station, to_station, fare_poisha, distance_m, status,
                                 purchased_at, expires_at, cancelled_at)
            VALUES ({id}, {userId}, 'uttara-north', 'motijheel', {fare}, 16000, {status},
                    {expiresAt.AddDays(-1)}, {expiresAt}, {cancelledAt})
            """, Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    private Task<ExpiryRun> RunJob(DateTimeOffset now, int batchSize = 100)
    {
        var ctx = db.CreateDbContext();
        return RunAndDispose();

        async Task<ExpiryRun> RunAndDispose()
        {
            await using (ctx)
            {
                return await TicketExpiry.RunAsync(ctx, now, batchSize, NullLogger.Instance, Ct);
            }
        }
    }

    private async Task<string> StatusOf(Guid ticketId)
    {
        await using var ctx = db.CreateDbContext();
        return (await ctx.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId, Ct)).Status.ToString();
    }

    /// <summary>Balance (checked against SUM(ledger)) and the refund rows' amounts.</summary>
    private async Task<(long Balance, List<long> Refunds)> Books(Guid walletId)
    {
        await using var ctx = db.CreateDbContext();
        long balance = await ctx.Wallets.Where(w => w.Id == walletId).Select(w => w.BalancePoisha).SingleAsync(Ct);
        long sum = await ctx.LedgerEntries.Where(e => e.WalletId == walletId).SumAsync(e => e.AmountPoisha, Ct);
        sum.ShouldBe(balance);
        List<long> refunds = await ctx.LedgerEntries
            .Where(e => e.WalletId == walletId && e.Type == LedgerEntryType.Refund)
            .OrderBy(e => e.Id).Select(e => e.AmountPoisha).ToListAsync(Ct);
        return (balance, refunds);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    // ---------- the policy ----------

    [Fact]
    public async Task An_unused_ticket_past_its_day_is_expired_and_its_fare_refunded_once()
    {
        DateTimeOffset now = Era(1991);
        var (userId, walletId) = await UserWith(500_00);
        Guid a = await Ticket(userId, now.AddHours(-1));
        Guid b = await Ticket(userId, now.AddHours(-2), fare: 60_00);
        (await Books(walletId)).Balance.ShouldBe(340_00);

        ExpiryRun first = await RunJob(now);
        ExpiryRun second = await RunJob(now); // a later run (or another instance) finds nothing left

        first.ShouldBe(new ExpiryRun(2, 160_00, 0));
        second.ShouldBe(new ExpiryRun(0, 0, 0));
        (await StatusOf(a)).ShouldBe("Expired");
        (await StatusOf(b)).ShouldBe("Expired");
        var (balance, refunds) = await Books(walletId);
        balance.ShouldBe(500_00);
        refunds.Order().ShouldBe([60_00, Fare]);

        await using var ctx = db.CreateDbContext();
        var rows = await ctx.LedgerEntries.AsNoTracking()
            .Where(e => e.WalletId == walletId && e.Type == LedgerEntryType.Refund).ToListAsync(Ct);
        rows.Select(e => e.RefId).Order().ShouldBe(new[] { a, b }.Order()); // each refund points at its ticket
        rows.ShouldAllBe(e => e.CreatedAt == now);
    }

    [Fact]
    public async Task A_ticket_whose_day_ends_exactly_now_expires_but_one_a_moment_later_does_not()
    {
        DateTimeOffset now = Era(1992);
        var (userId, walletId) = await UserWith(500_00);
        Guid onTheDot = await Ticket(userId, now);
        Guid justAfter = await Ticket(userId, now.AddTicks(10)); // timestamptz keeps microseconds
        Guid tomorrow = await Ticket(userId, DateTimeOffset.UtcNow.AddDays(1));

        (await RunJob(now)).ShouldBe(new ExpiryRun(1, Fare, 0));

        (await StatusOf(onTheDot)).ShouldBe("Expired");
        (await StatusOf(justAfter)).ShouldBe("Active");
        (await StatusOf(tomorrow)).ShouldBe("Active");
        (await Books(walletId)).ShouldSatisfyAllConditions(
            books => books.Balance.ShouldBe(300_00),
            books => books.Refunds.ShouldBe([Fare]));

        // Leave nothing Active in this year for later tests: one microsecond later its turn comes too.
        (await RunJob(now.AddSeconds(1))).ShouldBe(new ExpiryRun(1, Fare, 0));
        (await StatusOf(tomorrow)).ShouldBe("Active");
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Used")]
    [InlineData("InJourney")]
    [InlineData("Expired")]
    public async Task Tickets_that_already_left_active_are_never_touched(string status)
    {
        DateTimeOffset now = Era(1993);
        var (userId, walletId) = await UserWith(500_00);
        Guid ticket = await Ticket(userId, now.AddDays(-3), status);

        (await RunJob(now)).ShouldBe(new ExpiryRun(0, 0, 0));

        (await StatusOf(ticket)).ShouldBe(status);
        (await Books(walletId)).ShouldSatisfyAllConditions(
            books => books.Balance.ShouldBe(400_00),
            books => books.Refunds.ShouldBeEmpty());
    }

    // ---------- one bad row ----------

    [Fact]
    public async Task A_ticket_whose_refund_was_already_booked_is_left_alone_and_the_rest_still_expire()
    {
        // Someone (a bug, a hand-made fix) already wrote a Refund row for ticket "bad". The unique index
        // ux_ledger_entries_refund_ref refuses a second one: that ticket's step fails, only it rolls back.
        DateTimeOffset now = Era(1994);
        var (userId, walletId) = await UserWith(1000_00);
        Guid[] good = [await Ticket(userId, now.AddHours(-3)), await Ticket(userId, now.AddHours(-2))];
        Guid bad = await Ticket(userId, now.AddHours(-1));
        Guid more = await Ticket(userId, now.AddMinutes(-30));
        await using (var ctx = db.CreateDbContext())
        {
            (await LedgerBook.PostAsync(ctx, walletId, Fare, LedgerEntryType.Refund, bad, now, Ct)).IsSuccess.ShouldBeTrue();
        }

        // Batch size 2: the bad row sits in a full batch, so the loop must go on to the next batch after it.
        ExpiryRun run = await RunJob(now, batchSize: 2);

        run.ShouldBe(new ExpiryRun(3, 3 * Fare, 1));
        (await StatusOf(bad)).ShouldBe("Active"); // retried next run, never half-done
        foreach (Guid id in good.Append(more))
        {
            (await StatusOf(id)).ShouldBe("Expired");
        }

        var (balance, refunds) = await Books(walletId);
        balance.ShouldBe(1000_00); // 4 fares out, 1 hand-made + 3 job refunds back in
        refunds.Count.ShouldBe(4);

        // Every run tries it again and fails the same safe way; nothing else changes. Then clean up this year.
        (await RunJob(now)).ShouldBe(new ExpiryRun(0, 0, 1));
        await using (var ctx = db.CreateDbContext())
        {
            await ctx.Database.ExecuteSqlAsync($"UPDATE tickets SET status = 'Expired' WHERE id = {bad}", Ct);
        }
    }

    [Fact]
    public async Task A_ticket_whose_owner_has_no_wallet_stays_active_and_does_not_block_the_others()
    {
        DateTimeOffset now = Era(1995);
        Guid orphanOwner = Guid.CreateVersion7(); // no wallet: against the rules, but the job must survive it
        Guid orphan = await Ticket(orphanOwner, now.AddHours(-5), pay: false);
        var (userId, walletId) = await UserWith(500_00);
        Guid[] tickets = [await Ticket(userId, now.AddHours(-4)), await Ticket(userId, now.AddHours(-3)), await Ticket(userId, now.AddHours(-2))];

        (await RunJob(now, batchSize: 1)).ShouldBe(new ExpiryRun(3, 3 * Fare, 1));

        (await StatusOf(orphan)).ShouldBe("Active");
        foreach (Guid id in tickets)
        {
            (await StatusOf(id)).ShouldBe("Expired");
        }

        (await Books(walletId)).Balance.ShouldBe(500_00);

        // Once the wallet exists, the next run picks the ticket up: a failure is retried, not forgotten.
        Guid orphanWallet;
        await using (var ctx = db.CreateDbContext())
        {
            orphanWallet = (await WalletEndpoints.GetOrCreateAsync(ctx, orphanOwner, DateTimeOffset.UtcNow, Ct)).Id;
        }

        (await RunJob(now)).ShouldBe(new ExpiryRun(1, Fare, 0));
        (await StatusOf(orphan)).ShouldBe("Expired");
        (await Books(orphanWallet)).Balance.ShouldBe(Fare);
    }

    // ---------- many instances ----------

    [Fact]
    public async Task Four_instances_over_200_due_tickets_refund_each_exactly_once()
    {
        // 20 passengers with 10 overdue tickets each. Four "Core instances" (each its own connection) run the job
        // at the same moment with small batches, so their batches overlap on the same wallets all the time.
        DateTimeOffset now = Era(1996);
        var users = new List<(Guid UserId, Guid WalletId)>();
        var tickets = new List<Guid>();
        for (int u = 0; u < 20; u++)
        {
            var user = await UserWith(1000_00);
            users.Add(user);
            for (int t = 0; t < 10; t++)
            {
                tickets.Add(await Ticket(user.UserId, now.AddMinutes(-(u * 10) - t - 1)));
            }
        }

        using var start = new SemaphoreSlim(0);
        Task<ExpiryRun>[] workers = [.. Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await start.WaitAsync(Ct);
            return await RunJob(now, batchSize: 15);
        }, Ct))];
        start.Release(4);
        ExpiryRun[] runs = await Task.WhenAll(workers); // a deadlock would surface here as a 40P01 exception

        runs.Sum(r => r.Expired).ShouldBe(200);
        runs.Sum(r => r.RefundedPoisha).ShouldBe(200 * Fare);
        runs.ShouldAllBe(r => r.Failed == 0);

        await using var ctx = db.CreateDbContext();
        (await ctx.Tickets.CountAsync(t => tickets.Contains(t.Id) && t.Status == TicketStatus.Expired, Ct)).ShouldBe(200);
        var refundsPerTicket = await ctx.LedgerEntries
            .Where(e => e.Type == LedgerEntryType.Refund && tickets.Contains(e.RefId))
            .GroupBy(e => e.RefId).Select(g => g.Count()).ToListAsync(Ct);
        refundsPerTicket.Count.ShouldBe(200);
        refundsPerTicket.ShouldAllBe(c => c == 1);
        foreach (var (_, walletId) in users)
        {
            (await Books(walletId)).Balance.ShouldBe(1000_00); // SUM(ledger) == balance is checked inside
        }
    }

    // ---------- the rest of Core sees it ----------

    [Fact]
    public async Task An_expired_ticket_can_no_longer_be_cancelled_or_shown_as_a_qr()
    {
        DateTimeOffset now = Era(1997);
        var (userId, walletId) = await UserWith(500_00);
        Guid ticket = await Ticket(userId, now.AddHours(-1));
        (await RunJob(now)).Expired.ShouldBe(1);
        using HttpClient client = _factory.ClientFor(userId);

        var cancel = await client.PostAsync($"/api/v1/tickets/{ticket}/cancel", null, Ct);
        var credential = await client.GetAsync($"/api/v1/tickets/{ticket}/credential", Ct);
        var shown = await client.GetFromJsonAsync<TicketResponse>($"/api/v1/tickets/{ticket}", Ct);

        cancel.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(cancel)).ShouldBe("ticket.not_cancellable");
        credential.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(credential)).ShouldBe("ticket.not_usable");
        shown!.Status.ShouldBe("Expired");
        (await Books(walletId)).Refunds.ShouldBe([Fare]); // the cancel did not refund a second time
    }

    [Fact]
    public async Task The_batch_query_can_read_due_tickets_from_the_partial_index()
    {
        // Few rows in a test database, so the planner may prefer a table scan; turning that off shows the
        // partial index CAN serve the query (its WHERE matches the index's filter and order).
        await using var ctx = db.CreateDbContext();
        await using var tx = await ctx.Database.BeginTransactionAsync(Ct);
        await ctx.Database.ExecuteSqlAsync($"SET LOCAL enable_seqscan = off", Ct);
        DateTimeOffset now = Era(1990);
        Guid[] skip = [];
        List<string> plan = await ctx.Database.SqlQuery<string>($"""
            EXPLAIN SELECT t.id FROM tickets t LEFT JOIN wallets w ON w.user_id = t.user_id
             WHERE t.status = 'Active' AND t.expires_at <= {now} AND NOT (t.id = ANY({skip}))
             ORDER BY t.expires_at LIMIT 100 FOR UPDATE OF t SKIP LOCKED
            """).ToListAsync(Ct);

        string.Join('\n', plan).ShouldContain("ix_tickets_active_expiry");
    }

    // ---------- the hosted worker ----------

    /// <summary>The real clock's timers, but "now" frozen at a chosen instant.</summary>
    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task The_background_worker_expires_due_tickets_on_its_own_and_stops_cleanly()
    {
        DateTimeOffset now = Era(1998);
        var (userId, walletId) = await UserWith(500_00);
        Guid[] tickets = [await Ticket(userId, now.AddHours(-2)), await Ticket(userId, now.AddHours(-1))];

        var worker = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Jobs:ExpiryEnabled", "true");
            b.UseSetting("Jobs:ExpiryInterval", "00:00:00.200");
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(new FrozenClock(now)));
        });
        try
        {
            _ = worker.Services; // starts the host, and with it the worker

            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while ((await Books(walletId)).Refunds.Count < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, Ct);
            }

            // A third ticket after the first run: the timer brings the worker back for it.
            Guid late = await Ticket(userId, now.AddMinutes(-1));
            deadline = DateTime.UtcNow.AddSeconds(15);
            while (await StatusOf(late) == "Active" && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, Ct);
            }

            foreach (Guid id in tickets.Append(late))
            {
                (await StatusOf(id)).ShouldBe("Expired");
            }

            (await Books(walletId)).ShouldSatisfyAllConditions(
                books => books.Balance.ShouldBe(500_00),
                books => books.Refunds.Count.ShouldBe(3));
        }
        finally
        {
            await worker.DisposeAsync(); // graceful shutdown: StopAsync cancels the worker; it must not throw
        }
    }

    [Fact]
    public async Task The_worker_does_nothing_when_switched_off()
    {
        DateTimeOffset now = Era(1999);
        var (userId, walletId) = await UserWith(500_00);
        Guid ticket = await Ticket(userId, now.AddHours(-1));

        await using (var off = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(new FrozenClock(now)))))
        {
            _ = off.Services; // CoreFactory sets Jobs:ExpiryEnabled = false
            await Task.Delay(500, Ct);
        }

        (await StatusOf(ticket)).ShouldBe("Active");
        (await Books(walletId)).Refunds.ShouldBeEmpty();
        (await RunJob(now)).Expired.ShouldBe(1); // clean up this year
    }

    [Theory]
    [InlineData("Jobs:ExpiryBatchSize", "0")]
    [InlineData("Jobs:ExpiryBatchSize", "1001")]
    [InlineData("Jobs:ExpiryInterval", "00:00:00")]
    [InlineData("Jobs:ExpiryInterval", "-00:01:00")]
    public async Task A_bad_job_setting_stops_core_at_startup(string key, string value)
    {
        await using var bad = _factory.WithWebHostBuilder(b => b.UseSetting(key, value));

        var ex = Should.Throw<OptionsValidationException>(() => _ = bad.Services);

        ex.Message.ShouldContain("Jobs:");
    }
}
