using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TransitPass.Identity.Api.Users;

namespace TransitPass.Identity.Api.Sessions;

/// <summary>
/// One refresh token. One row of the <c>refresh_tokens</c> table. The token itself is never stored, only its
/// SHA-256 hash, so a leaked database (or backup) gives nobody a working token.
/// Tokens form a chain per login: login creates the first one (the family root, whose id is the family id),
/// each refresh marks the presented token used and adds its successor to the same family.
/// </summary>
public sealed class RefreshToken
{
    /// <summary>UUIDv7.</summary>
    public required Guid Id { get; init; }

    /// <summary>Whose session this is.</summary>
    public required Guid UserId { get; init; }

    /// <summary>The id of the first token of this login. Every rotation of one login shares it.</summary>
    public required Guid FamilyId { get; init; }

    /// <summary>SHA-256 of the token's 32 random bytes: always 32 bytes. Unique.</summary>
    public required byte[] TokenHash { get; init; }

    /// <summary>When it was issued (UTC).</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>After this moment the token is refused.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>When it was exchanged for its successor. Set once; a second use is a theft signal.</summary>
    public DateTimeOffset? UsedAt { get; init; }

    /// <summary>When its family was revoked (logout or reuse detected).</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>The token that replaced this one. Set together with <see cref="UsedAt"/>.</summary>
    public Guid? ReplacedBy { get; init; }
}

/// <summary>How <see cref="RefreshToken"/> maps to the <c>refresh_tokens</c> table.</summary>
internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens", t =>
        {
            // Only a SHA-256 digest fits. A writer that tried to store the raw token (43 chars) is refused.
            t.HasCheckConstraint("ck_refresh_tokens_token_hash_sha256", "octet_length(token_hash) = 32");
            t.HasCheckConstraint("ck_refresh_tokens_expires_after_created", "expires_at > created_at");
            // "Used" and "has a successor" are one fact: both set together, or neither.
            t.HasCheckConstraint("ck_refresh_tokens_used_has_successor", "(used_at IS NULL) = (replaced_by IS NULL)");
        });

        builder.HasKey(r => r.Id).HasName("pk_refresh_tokens");
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.UserId).HasColumnName("user_id");
        builder.Property(r => r.FamilyId).HasColumnName("family_id");
        builder.Property(r => r.TokenHash).HasColumnName("token_hash");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");
        builder.Property(r => r.ExpiresAt).HasColumnName("expires_at");
        builder.Property(r => r.UsedAt).HasColumnName("used_at");
        builder.Property(r => r.RevokedAt).HasColumnName("revoked_at");
        builder.Property(r => r.ReplacedBy).HasColumnName("replaced_by");

        // A deleted user takes their sessions with them.
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.UserId)
            .HasConstraintName("fk_refresh_tokens_users_user_id").OnDelete(DeleteBehavior.Cascade);

        // The family id is a real token: the root. Its row is also the family's lock (see RefreshTokens).
        // NO ACTION, not RESTRICT: Postgres checks NO ACTION at the end of the statement, so deleting a user can
        // cascade through a whole family in one DELETE (root and children together) without tripping these.
        builder.HasOne<RefreshToken>().WithMany().HasForeignKey(r => r.FamilyId)
            .HasConstraintName("fk_refresh_tokens_family_id").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<RefreshToken>().WithOne().HasForeignKey<RefreshToken>(r => r.ReplacedBy)
            .HasConstraintName("fk_refresh_tokens_replaced_by").OnDelete(DeleteBehavior.NoAction);

        // The lookup on every refresh: hash → row. Unique: two tokens with one hash would be a broken generator.
        builder.HasIndex(r => r.TokenHash).IsUnique().HasDatabaseName("ux_refresh_tokens_token_hash");
        // Revoke a whole family in one indexed UPDATE.
        builder.HasIndex(r => r.FamilyId).HasDatabaseName("ix_refresh_tokens_family_id");
        // Each token has at most one parent (and the FK above needs an index on its column anyway).
        builder.HasIndex(r => r.ReplacedBy).IsUnique().HasDatabaseName("ux_refresh_tokens_replaced_by");
        // FK column index: deleting a user finds their tokens without a full scan.
        builder.HasIndex(r => r.UserId).HasDatabaseName("ix_refresh_tokens_user_id");
    }
}
