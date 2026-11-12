using Microsoft.EntityFrameworkCore;
using TransitPass.Core.Api.Idempotency;
using TransitPass.Core.Api.Ledger;
using TransitPass.Core.Api.Payments;
using TransitPass.Core.Api.Tickets;
using TransitPass.Core.Api.Topups;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Api.Data;

/// <summary>The Core service's unit of work over core_db.</summary>
public sealed class CoreDbContext(DbContextOptions<CoreDbContext> options) : DbContext(options)
{
    public DbSet<Wallet> Wallets => Set<Wallet>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    public DbSet<Topup> Topups => Set<Topup>();

    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CoreDbContext).Assembly);
    }
}
