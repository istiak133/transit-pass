using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TransitPass.Core.Api.Payments;

/// <summary>
/// The inbox: one row per payment notification we have processed. Providers deliver "at least once",
/// so the same event can arrive twice (a retry after a timeout, a replay). The primary key
/// (provider, event_id) makes the second arrival a no-op, decided atomically by the database.
/// </summary>
public sealed class PaymentEvent
{
    public required string Provider { get; init; }

    public required string EventId { get; init; }

    public required string Type { get; init; }

    /// <summary>The top-up the event was about. No FK: this is the provider's claim, kept as received.</summary>
    public required Guid TopupId { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }
}

/// <summary>Maps <see cref="PaymentEvent"/> to the payment_events table.</summary>
internal sealed class PaymentEventConfiguration : IEntityTypeConfiguration<PaymentEvent>
{
    public void Configure(EntityTypeBuilder<PaymentEvent> builder)
    {
        builder.ToTable("payment_events");
        builder.HasKey(e => new { e.Provider, e.EventId }).HasName("pk_payment_events");
        builder.Property(e => e.Provider).HasColumnName("provider").HasMaxLength(32);
        builder.Property(e => e.EventId).HasColumnName("event_id").HasMaxLength(100);
        builder.Property(e => e.Type).HasColumnName("type").HasMaxLength(32);
        builder.Property(e => e.TopupId).HasColumnName("topup_id");
        builder.Property(e => e.ReceivedAt).HasColumnName("received_at");
    }
}
