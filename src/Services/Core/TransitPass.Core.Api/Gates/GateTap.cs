using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TransitPass.Core.Api.Tickets;

namespace TransitPass.Core.Api.Gates;

/// <summary>Which way through the gate.</summary>
public enum GateDirection
{
    Entry,
    Exit,
}

/// <summary>
/// "Gate G sent tap T about ticket X, and we decided D." The gate makes up <see cref="TapId"/> itself (a UUID per
/// physical tap) and resends it after a timeout; the resend finds this row and gets the same decision back,
/// so one tap can never open the gate twice or charge twice. Written in the SAME transaction as the ticket change
/// for an allowed tap; on its own for a refused one.
/// </summary>
public sealed class GateTap
{
    public required Guid TapId { get; init; }

    public required string GateId { get; init; }

    public required string Station { get; init; }

    public required GateDirection Direction { get; init; }

    public required Guid TicketId { get; init; }

    /// <summary>SHA-256 of (gate, station, direction, credential). The same tap id with a different request is refused.</summary>
    public required string RequestHash { get; init; }

    public required bool Allowed { get; init; }

    /// <summary>The decision's code, e.g. gate.entered or gate.already_inside (handy for queries and reports).</summary>
    public required string Result { get; init; }

    /// <summary>The whole decision as JSON (jsonb), replayed for a repeated tap id.</summary>
    public required string Response { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Maps <see cref="GateTap"/> to the gate_taps table.</summary>
internal sealed class GateTapConfiguration : IEntityTypeConfiguration<GateTap>
{
    public void Configure(EntityTypeBuilder<GateTap> builder)
    {
        builder.ToTable("gate_taps", t =>
        {
            t.HasCheckConstraint("ck_gate_taps_direction", "direction IN ('Entry', 'Exit')");
            // An allowed tap always says "gate.entered" or "gate.exited"; a refusal never does.
            t.HasCheckConstraint("ck_gate_taps_allowed_result", "allowed = (result IN ('gate.entered', 'gate.exited'))");
        });

        // The tap id IS the primary key: a resent tap collides here (ON CONFLICT DO NOTHING) and is answered from the row.
        builder.HasKey(t => t.TapId).HasName("pk_gate_taps");
        builder.Property(t => t.TapId).HasColumnName("tap_id").ValueGeneratedNever();
        builder.Property(t => t.GateId).HasColumnName("gate_id").HasMaxLength(64);
        builder.Property(t => t.Station).HasColumnName("station").HasMaxLength(64);
        builder.Property(t => t.Direction).HasColumnName("direction").HasConversion<string>().HasMaxLength(8);
        builder.Property(t => t.TicketId).HasColumnName("ticket_id");
        builder.Property(t => t.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsFixedLength();
        builder.Property(t => t.Allowed).HasColumnName("allowed");
        builder.Property(t => t.Result).HasColumnName("result").HasMaxLength(32);
        builder.Property(t => t.Response).HasColumnName("response").HasColumnType("jsonb");
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");

        // "Every tap of this ticket, in order" (support and fraud review). Also serves the FK.
        builder.HasIndex(t => new { t.TicketId, t.CreatedAt }).HasDatabaseName("ix_gate_taps_ticket_id_created_at");
        builder.HasOne<Ticket>().WithMany().HasForeignKey(t => t.TicketId)
            .HasConstraintName("fk_gate_taps_ticket").OnDelete(DeleteBehavior.Restrict);
    }
}
