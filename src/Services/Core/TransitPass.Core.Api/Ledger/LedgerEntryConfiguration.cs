using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Api.Ledger;

/// <summary>Maps <see cref="LedgerEntry"/> to the ledger_entries table.</summary>
internal sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable("ledger_entries", t =>
        {
            t.HasCheckConstraint("ck_ledger_entries_amount_non_zero", "amount_poisha <> 0");
            t.HasCheckConstraint("ck_ledger_entries_type",
                "type IN ('TopUp', 'Purchase', 'Refund', 'Surcharge')");
        });

        builder.HasKey(e => e.Id).HasName("pk_ledger_entries");
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.WalletId).HasColumnName("wallet_id");
        builder.Property(e => e.AmountPoisha).HasColumnName("amount_poisha");
        builder.Property(e => e.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.RefId).HasColumnName("ref_id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        // A wallet with history can't be deleted (RESTRICT), so money never disappears.
        builder.HasOne<Wallet>().WithMany().HasForeignKey(e => e.WalletId)
            .HasConstraintName("fk_ledger_entries_wallet").OnDelete(DeleteBehavior.Restrict);

        // Composite (wallet_id, id): "this wallet's entries, newest first, after cursor X" is one index range
        // scan, O(log n + page). It also serves the FK, so no separate wallet_id index is needed.
        builder.HasIndex(e => new { e.WalletId, e.Id }).HasDatabaseName("ix_ledger_entries_wallet_id_id");
    }
}
