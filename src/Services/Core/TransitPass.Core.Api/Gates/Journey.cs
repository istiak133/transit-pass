using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TransitPass.Core.Api.Tickets;

namespace TransitPass.Core.Api.Gates;

/// <summary>A journey is open from the entry tap until the exit tap. Arrows only go forward.</summary>
public enum JourneyStatus
{
    InProgress,
    Completed,
}

/// <summary>
/// One ride on one single-journey ticket: where and when Rafi went in, and (once he is out) where, when and what
/// extra he paid. At most one per ticket, ever (UNIQUE ticket_id): a single-journey ticket rides once.
/// </summary>
public sealed class Journey
{
    public required Guid Id { get; init; }

    public required Guid TicketId { get; init; }

    /// <summary>The station of the gate he entered through (any station; see <see cref="GateTaps"/>).</summary>
    public required string EntryStation { get; init; }

    public required Guid EntryTapId { get; init; }

    public required DateTimeOffset EnteredAt { get; init; }

    /// <summary>The four exit fields are all null while InProgress and all set once Completed (CHECKs enforce it).</summary>
    public string? ExitStation { get; set; }

    public Guid? ExitTapId { get; set; }

    public DateTimeOffset? ExitedAt { get; set; }

    /// <summary>What the exit charged on top of the ticket's fare (0 for the bought trip or a shorter one).</summary>
    public long? SurchargePoisha { get; set; }

    public JourneyStatus Status { get; set; }
}

/// <summary>Maps <see cref="Journey"/> to the journeys table.</summary>
internal sealed class JourneyConfiguration : IEntityTypeConfiguration<Journey>
{
    public void Configure(EntityTypeBuilder<Journey> builder)
    {
        builder.ToTable("journeys", t =>
        {
            t.HasCheckConstraint("ck_journeys_status", "status IN ('InProgress', 'Completed')");
            // The exit is recorded all at once or not at all: never a station without a time, or a time without a charge.
            t.HasCheckConstraint("ck_journeys_exit_all_or_nothing",
                "num_nonnulls(exit_station, exit_tap_id, exited_at, surcharge_poisha) IN (0, 4)");
            t.HasCheckConstraint("ck_journeys_completed_has_exit", "(status = 'Completed') = (exited_at IS NOT NULL)");
            t.HasCheckConstraint("ck_journeys_surcharge_not_negative", "surcharge_poisha >= 0");
            t.HasCheckConstraint("ck_journeys_exit_after_entry", "exited_at >= entered_at");
        });

        builder.HasKey(j => j.Id).HasName("pk_journeys");
        builder.Property(j => j.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(j => j.TicketId).HasColumnName("ticket_id");
        builder.Property(j => j.EntryStation).HasColumnName("entry_station").HasMaxLength(64);
        builder.Property(j => j.EntryTapId).HasColumnName("entry_tap_id");
        builder.Property(j => j.EnteredAt).HasColumnName("entered_at");
        builder.Property(j => j.ExitStation).HasColumnName("exit_station").HasMaxLength(64);
        builder.Property(j => j.ExitTapId).HasColumnName("exit_tap_id");
        builder.Property(j => j.ExitedAt).HasColumnName("exited_at");
        builder.Property(j => j.SurchargePoisha).HasColumnName("surcharge_poisha");
        builder.Property(j => j.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);

        // One journey per ticket. Also the index the exit uses to find the open journey: O(log n).
        builder.HasIndex(j => j.TicketId).IsUnique().HasDatabaseName("ux_journeys_ticket_id");
        builder.HasOne<Ticket>().WithMany().HasForeignKey(j => j.TicketId)
            .HasConstraintName("fk_journeys_ticket").OnDelete(DeleteBehavior.Restrict);
    }
}
