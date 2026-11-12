using Microsoft.EntityFrameworkCore;
using TransitPass.Core.Api.Ledger;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Api.Data;

/// <summary>The Core service's unit of work over core_db.</summary>
public sealed class CoreDbContext(DbContextOptions<CoreDbContext> options) : DbContext(options)
{
    public DbSet<Wallet> Wallets => Set<Wallet>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CoreDbContext).Assembly);
    }
}
