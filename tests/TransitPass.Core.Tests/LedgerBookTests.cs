using Microsoft.EntityFrameworkCore;
using Npgsql;
using TransitPass.Core.Api.Data;
using TransitPass.Core.Api.Ledger;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Tests;

public sealed class LedgerBookTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> NewWallet()
    {
        await using var ctx = db.CreateDbContext();
        return (await WalletEndpoints.GetOrCreateAsync(ctx, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Ct)).Id;
    }

    private async Task<Result> Post(Guid walletId, long amount, LedgerEntryType type = LedgerEntryType.TopUp)
    {
        await using var ctx = db.CreateDbContext();
        var r = await LedgerBook.PostAsync(ctx, walletId, amount, type, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Ct);
        return new Result(r.IsSuccess, r.IsSuccess ? r.Value : null, r.IsSuccess ? null : r.Error.Code);
    }

    private sealed record Result(bool Ok, long? Balance, string? Code);

    /// <summary>The ledger's core promise: replaying every entry gives exactly the stored balance.</summary>
    private async Task ShouldBalance(Guid walletId, long expected)
    {
        await using var ctx = db.CreateDbContext();
        long balance = await ctx.Wallets.Where(w => w.Id == walletId).Select(w => w.BalancePoisha).SingleAsync(Ct);
        long sum = await ctx.LedgerEntries.Where(e => e.WalletId == walletId).SumAsync(e => e.AmountPoisha, Ct);
        balance.ShouldBe(expected);
        sum.ShouldBe(balance);
    }

    [Fact]
    public async Task Credit_then_debit_updates_balance_and_history_together()
    {
        Guid w = await NewWallet();

        (await Post(w, 50_000)).Balance.ShouldBe(50_000);
        (await Post(w, -10_000, LedgerEntryType.Purchase)).Balance.ShouldBe(40_000);

        await ShouldBalance(w, 40_000);
    }

    [Fact]
    public async Task Overdraft_is_refused_and_leaves_no_trace()
    {
        Guid w = await NewWallet();
        await Post(w, 5_000);

        var r = await Post(w, -10_000, LedgerEntryType.Purchase);

        r.Ok.ShouldBeFalse();
        r.Code.ShouldBe("wallet.insufficient_funds");
        await ShouldBalance(w, 5_000); // and still only the one entry
    }

    [Fact]
    public async Task Twenty_racing_debits_on_money_for_ten_never_overspend()
    {
        Guid w = await NewWallet();
        await Post(w, 100_000); // ৳1000: enough for exactly ten ৳100 debits

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => Post(w, -10_000, LedgerEntryType.Purchase), Ct)));

        results.Count(r => r.Ok).ShouldBe(10);
        results.Count(r => r.Code == "wallet.insufficient_funds").ShouldBe(10);
        await ShouldBalance(w, 0);
    }

    [Fact]
    public async Task Postings_inside_a_callers_transaction_roll_back_with_it()
    {
        Guid w = await NewWallet();
        await using (var ctx = db.CreateDbContext())
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(Ct);
            (await LedgerBook.PostAsync(ctx, w, 9_000, LedgerEntryType.TopUp, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Ct))
                .IsSuccess.ShouldBeTrue();
            await tx.RollbackAsync(Ct); // e.g. a later step of a purchase failed
        }

        await ShouldBalance(w, 0); // neither the balance nor the entry survived
    }

    [Fact]
    public async Task The_database_itself_refuses_a_negative_balance()
    {
        Guid w = await NewWallet();
        await using var ctx = db.CreateDbContext();

        var ex = await Should.ThrowAsync<PostgresException>(() =>
            ctx.Database.ExecuteSqlAsync($"UPDATE wallets SET balance_poisha = -1 WHERE id = {w}", Ct));

        ex.ConstraintName.ShouldBe("ck_wallets_balance_non_negative");
    }

    [Theory]
    [InlineData(0L, "TopUp", "ck_ledger_entries_amount_non_zero")]
    [InlineData(100L, "Gift", "ck_ledger_entries_type")]
    public async Task The_database_refuses_bad_ledger_rows(long amount, string type, string constraint)
    {
        Guid w = await NewWallet();
        await using CoreDbContext ctx = db.CreateDbContext();

        var ex = await Should.ThrowAsync<PostgresException>(() => ctx.Database.ExecuteSqlAsync(
            $"INSERT INTO ledger_entries VALUES (gen_random_uuid(), {w}, {amount}, {type}, gen_random_uuid(), now())", Ct));

        ex.ConstraintName.ShouldBe(constraint);
    }
}
