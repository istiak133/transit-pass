using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TransitPass.Network.Api.Stations;

/// <summary>How <see cref="Station"/> maps to the <c>stations</c> table: names, types, keys, indexes, seed rows.</summary>
internal sealed class StationConfiguration : IEntityTypeConfiguration<Station>
{
    public void Configure(EntityTypeBuilder<Station> builder)
    {
        builder.ToTable("stations", t =>
            // Slugs only: lowercase letters, digits and single hyphens. The database refuses anything else.
            t.HasCheckConstraint("ck_stations_id_slug", "id ~ '^[a-z0-9]+(-[a-z0-9]+)*$'"));

        // PRIMARY KEY → PostgreSQL builds a unique B+ tree index on id automatically (pk_stations).
        builder.HasKey(s => s.Id).HasName("pk_stations");
        builder.Property(s => s.Id).HasColumnName("id").HasMaxLength(64);

        builder.Property(s => s.Name).HasColumnName("name").HasMaxLength(100).IsRequired();

        // Two stations with the same display name would confuse passengers, so make it impossible.
        // UNIQUE → a second B+ tree index, on name.
        builder.HasIndex(s => s.Name).IsUnique().HasDatabaseName("ux_stations_name");

        // Seed rows live in the migration, so every environment starts with the same stations.
        builder.HasData(
            new Station { Id = "uttara-north", Name = "Uttara North" },
            new Station { Id = "agargaon", Name = "Agargaon" },
            new Station { Id = "karwan-bazar", Name = "Karwan Bazar" },
            new Station { Id = "motijheel", Name = "Motijheel" });
    }
}
