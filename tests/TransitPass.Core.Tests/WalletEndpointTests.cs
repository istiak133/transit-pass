using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TransitPass.Core.Api.Ledger;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Tests;

public sealed class WalletEndpointTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Me = "/api/v1/wallet/me";
    private const string History = "/api/v1/wallet/me/transactions";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private CoreFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new CoreFactory(db);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private sealed record Page(List<LedgerEntryResponse> Items, string? NextCursor);

    [Fact]
    public async Task First_visit_creates_an_empty_wallet_and_later_visits_return_the_same_one()
    {
        Guid userId = Guid.CreateVersion7();
        var client = _factory.ClientFor(userId);

        var first = await client.GetFromJsonAsync<WalletResponse>(Me, Ct);
        var second = await client.GetFromJsonAsync<WalletResponse>(Me, Ct);

        first.ShouldNotBeNull();
        first.UserId.ShouldBe(userId);
        first.BalancePoisha.ShouldBe(0);
        first.Currency.ShouldBe("BDT");
        second!.WalletId.ShouldBe(first.WalletId);
    }

    [Fact]
    public async Task Twenty_simultaneous_first_visits_create_exactly_one_wallet()
    {
        Guid userId = Guid.CreateVersion7();
        var client = _factory.ClientFor(userId);

        var wallets = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => client.GetFromJsonAsync<WalletResponse>(Me, Ct)));

        wallets.Select(w => w!.WalletId).Distinct().Count().ShouldBe(1);
        await using var ctx = db.CreateDbContext();
        (await ctx.Wallets.CountAsync(w => w.UserId == userId, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Balance_shows_what_the_ledger_posted()
    {
        Guid userId = Guid.CreateVersion7();
        Guid walletId = await NewWalletWithEntries(userId, 50_000, -10_000);

        var wallet = await _factory.ClientFor(userId).GetFromJsonAsync<WalletResponse>(Me, Ct);

        wallet!.WalletId.ShouldBe(walletId);
        wallet.BalancePoisha.ShouldBe(40_000); // ৳400.00
    }

    [Fact]
    public async Task History_without_a_wallet_is_empty_and_does_not_create_one()
    {
        Guid userId = Guid.CreateVersion7();

        var page = await _factory.ClientFor(userId).GetFromJsonAsync<Page>(History, Ct);

        page!.Items.ShouldBeEmpty();
        page.NextCursor.ShouldBeNull();
        await using var ctx = db.CreateDbContext();
        (await ctx.Wallets.AnyAsync(w => w.UserId == userId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task History_pages_newest_first_with_no_gaps_or_duplicates()
    {
        Guid userId = Guid.CreateVersion7();
        await NewWalletWithEntries(userId, 1_000, 2_000, 3_000, 4_000, 5_000);
        var client = _factory.ClientFor(userId);

        var seen = new List<long>();
        var cursors = new List<string?>();
        string url = $"{History}?limit=2";
        while (true)
        {
            var page = await client.GetFromJsonAsync<Page>(url, Ct);
            seen.AddRange(page!.Items.Select(i => i.AmountPoisha));
            cursors.Add(page.NextCursor);
            if (page.NextCursor is null)
            {
                break;
            }

            url = $"{History}?limit=2&after={page.NextCursor}";
        }

        seen.ShouldBe([5_000, 4_000, 3_000, 2_000, 1_000]); // newest first, each exactly once
        cursors.Count.ShouldBe(3);                           // 2 + 2 + 1
        cursors[^1].ShouldBeNull();                          // the last page says "no more"
    }

    [Fact]
    public async Task History_shows_only_the_callers_own_entries()
    {
        Guid rafi = Guid.CreateVersion7(), zara = Guid.CreateVersion7();
        await NewWalletWithEntries(rafi, 50_000);
        await NewWalletWithEntries(zara, 7_000);

        var page = await _factory.ClientFor(zara).GetFromJsonAsync<Page>(History, Ct);

        page!.Items.Select(i => i.AmountPoisha).ShouldBe([7_000]);
        page.Items[0].Type.ShouldBe("TopUp");
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAA")] // right length, wrong version byte
    public async Task A_bad_cursor_is_400_not_500(string cursor)
    {
        var response = await _factory.ClientFor(Guid.CreateVersion7()).GetAsync($"{History}?after={cursor}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString()
            .ShouldBe("pagination.invalid_cursor");
    }

    /// <summary>Creates the user's wallet and posts entries one second apart (so their order is known).</summary>
    private async Task<Guid> NewWalletWithEntries(Guid userId, params long[] amounts)
    {
        await using var ctx = db.CreateDbContext();
        DateTimeOffset t = DateTimeOffset.UtcNow;
        Wallet wallet = await WalletEndpoints.GetOrCreateAsync(ctx, userId, t, Ct);
        foreach (long amount in amounts)
        {
            t = t.AddSeconds(1);
            var type = amount > 0 ? LedgerEntryType.TopUp : LedgerEntryType.Purchase;
            (await LedgerBook.PostAsync(ctx, wallet.Id, amount, type, Guid.CreateVersion7(), t, Ct)).IsSuccess.ShouldBeTrue();
        }

        return wallet.Id;
    }
}
