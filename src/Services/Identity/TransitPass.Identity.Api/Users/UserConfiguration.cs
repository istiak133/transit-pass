using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TransitPass.Identity.Api.Users;

/// <summary>How <see cref="User"/> maps to the <c>users</c> table.</summary>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", t =>
            // Only normalized E.164 numbers reach the table. Any writer that skips normalization is refused.
            t.HasCheckConstraint("ck_users_phone_e164", "phone ~ '^\\+[1-9][0-9]{7,14}$'"));

        builder.HasKey(u => u.Id).HasName("pk_users");
        builder.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever(); // we generate UUIDv7 in code

        builder.Property(u => u.Phone).HasColumnName("phone").HasMaxLength(16);
        builder.Property(u => u.Name).HasColumnName("name").HasMaxLength(100);
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(200);
        builder.Property(u => u.CreatedAt).HasColumnName("created_at");

        // One phone = one account. Enforced by the B+ tree at the leaf, even when two sign-ups race.
        builder.HasIndex(u => u.Phone).IsUnique().HasDatabaseName("ux_users_phone");
    }
}
