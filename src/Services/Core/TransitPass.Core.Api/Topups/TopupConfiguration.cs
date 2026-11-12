using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TransitPass.Core.Api.Wallets;

namespace TransitPass.Core.Api.Topups;

/// <summary>Maps <see cref="Topup"/> to the topups table.</summary>
internal sealed class TopupConfiguration : IEntityTypeConfiguration<Topup>
{
    public void Configure(EntityTypeBuilder<Topup> builder)
    {
        builder.ToTable("topups", t =>
        {
            t.HasCheckConstraint("ck_topups_amount_positive", "amount_poisha > 0");
            t.HasCheckConstraint("ck_topups_status", "status IN ('Pending', 'Succeeded', 'Failed')");
            // The state and the timestamp can't disagree: Pending ⇔ not completed.
            t.HasCheckConstraint("ck_topups_completed_at", "(status = 'Pending') = (completed_at IS NULL)");
        });

        builder.HasKey(t => t.Id).HasName("pk_topups");
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(t => t.WalletId).HasColumnName("wallet_id");
        builder.Property(t => t.AmountPoisha).HasColumnName("amount_poisha");
        builder.Property(t => t.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.CompletedAt).HasColumnName("completed_at");

        builder.HasOne<Wallet>().WithMany().HasForeignKey(t => t.WalletId)
            .HasConstraintName("fk_topups_wallet").OnDelete(DeleteBehavior.Restrict);

        // Serves the FK and a future "my top-ups" page, newest first (same shape as the ledger index).
        builder.HasIndex(t => new { t.WalletId, t.Id }).HasDatabaseName("ix_topups_wallet_id_id");
    }
}
