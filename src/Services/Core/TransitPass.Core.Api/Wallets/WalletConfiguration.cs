using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TransitPass.Core.Api.Wallets;

/// <summary>Maps <see cref="Wallet"/> to the wallets table.</summary>
internal sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        builder.ToTable("wallets", t =>
            // The last line of defence: even buggy code can't make a balance negative.
            t.HasCheckConstraint("ck_wallets_balance_non_negative", "balance_poisha >= 0"));

        builder.HasKey(w => w.Id).HasName("pk_wallets");
        builder.Property(w => w.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(w => w.UserId).HasColumnName("user_id");
        builder.Property(w => w.BalancePoisha).HasColumnName("balance_poisha").HasDefaultValue(0L);
        builder.Property(w => w.CreatedAt).HasColumnName("created_at");

        // UNIQUE(user_id): one wallet per user (1:1), and the B-tree that answers "wallet of user X?" in O(log n).
        builder.HasIndex(w => w.UserId).IsUnique().HasDatabaseName("ux_wallets_user_id");
    }
}
