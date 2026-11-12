using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TransitPass.Core.Api.Tickets;

/// <summary>Maps <see cref="Ticket"/> to the tickets table.</summary>
internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets", t =>
        {
            t.HasCheckConstraint("ck_tickets_different_stations", "to_station <> from_station");
            t.HasCheckConstraint("ck_tickets_fare_positive", "fare_poisha > 0");
            t.HasCheckConstraint("ck_tickets_status",
                "status IN ('Active', 'InJourney', 'Used', 'Cancelled', 'Expired')");
            t.HasCheckConstraint("ck_tickets_expiry_after_purchase", "expires_at > purchased_at");
        });

        builder.HasKey(t => t.Id).HasName("pk_tickets");
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(t => t.UserId).HasColumnName("user_id");
        builder.Property(t => t.FromStation).HasColumnName("from_station").HasMaxLength(64);
        builder.Property(t => t.ToStation).HasColumnName("to_station").HasMaxLength(64);
        builder.Property(t => t.FarePoisha).HasColumnName("fare_poisha");
        builder.Property(t => t.DistanceM).HasColumnName("distance_m");
        builder.Property(t => t.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.PurchasedAt).HasColumnName("purchased_at");
        builder.Property(t => t.ExpiresAt).HasColumnName("expires_at");

        // "My tickets, newest first" (Phase 10's cursor page): O(log n + page).
        builder.HasIndex(t => new { t.UserId, t.Id }).HasDatabaseName("ix_tickets_user_id_id");

        // The expiry job (Phase 13) only cares about Active tickets. A partial index holds just those,
        // so it stays small however many used and cancelled tickets pile up.
        builder.HasIndex(t => t.ExpiresAt).HasFilter("status = 'Active'").HasDatabaseName("ix_tickets_active_expiry");
    }
}
