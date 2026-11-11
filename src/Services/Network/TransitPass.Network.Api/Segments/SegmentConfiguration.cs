using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TransitPass.Network.Api.Stations;

namespace TransitPass.Network.Api.Segments;

/// <summary>How <see cref="Segment"/> maps to the <c>segments</c> table.</summary>
internal sealed class SegmentConfiguration : IEntityTypeConfiguration<Segment>
{
    public void Configure(EntityTypeBuilder<Segment> builder)
    {
        builder.ToTable("segments", t =>
        {
            // Canonical order: one row per stretch. Blocks self-loops (a = b) and reversed duplicates (b, a).
            // COLLATE "C" compares bytes, matching C# string.CompareOrdinal. The default language-aware
            // collation can ignore hyphens, so it could order two slugs differently from our code.
            t.HasCheckConstraint("ck_segments_canonical_order", "station_a COLLATE \"C\" < station_b COLLATE \"C\"");
            t.HasCheckConstraint("ck_segments_distance_positive", "distance_m > 0");
        });

        // Composite PRIMARY KEY → one B+ tree on (station_a, station_b). It also serves lookups by station_a alone.
        builder.HasKey(s => new { s.StationA, s.StationB }).HasName("pk_segments");

        builder.Property(s => s.StationA).HasColumnName("station_a").HasMaxLength(64);
        builder.Property(s => s.StationB).HasColumnName("station_b").HasMaxLength(64);
        builder.Property(s => s.DistanceM).HasColumnName("distance_m");

        // Both ends must be real stations. RESTRICT: a station with track attached can't be deleted.
        builder.HasOne<Station>().WithMany().HasForeignKey(s => s.StationA)
            .HasConstraintName("fk_segments_station_a").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Station>().WithMany().HasForeignKey(s => s.StationB)
            .HasConstraintName("fk_segments_station_b").OnDelete(DeleteBehavior.Restrict);

        // PostgreSQL doesn't index FK columns. station_a is covered by the PK (first column); station_b isn't.
        builder.HasIndex(s => s.StationB).HasDatabaseName("ix_segments_station_b");

        builder.HasData(
            new Segment { StationA = "agargaon", StationB = "uttara-north", DistanceM = 9000 },
            new Segment { StationA = "agargaon", StationB = "karwan-bazar", DistanceM = 4000 },
            new Segment { StationA = "karwan-bazar", StationB = "motijheel", DistanceM = 3500 });
    }
}
