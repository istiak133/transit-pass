using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TransitPass.Core.Api.Idempotency;

/// <summary>
/// "This user already sent this Idempotency-Key, for this request, and got this answer."
/// The phone app retries a purchase after a timeout; the retry gets the stored answer instead of a second ticket.
/// The row is written in the SAME transaction as the debit and the ticket: all three exist, or none.
/// </summary>
public sealed class IdempotencyRecord
{
    public required Guid UserId { get; init; }

    public required string Key { get; init; }

    /// <summary>SHA-256 of the request (from, to). The same key with a different request is refused.</summary>
    public required string RequestHash { get; init; }

    public Guid? TicketId { get; set; }

    /// <summary>The exact JSON first returned, replayed byte for byte.</summary>
    public string? ResponseBody { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Maps <see cref="IdempotencyRecord"/> to the idempotency_keys table.</summary>
internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_keys", t =>
            t.HasCheckConstraint("ck_idempotency_keys_response_complete", "(ticket_id IS NULL) = (response_body IS NULL)"));

        // Keys are per user: two users may both send "abc" without colliding (and can't read each other's).
        builder.HasKey(r => new { r.UserId, r.Key }).HasName("pk_idempotency_keys");
        builder.Property(r => r.UserId).HasColumnName("user_id");
        builder.Property(r => r.Key).HasColumnName("key").HasMaxLength(64);
        builder.Property(r => r.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsFixedLength();
        builder.Property(r => r.TicketId).HasColumnName("ticket_id");
        builder.Property(r => r.ResponseBody).HasColumnName("response_body");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");
    }
}
