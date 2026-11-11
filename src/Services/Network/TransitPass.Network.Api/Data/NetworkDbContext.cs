using Microsoft.EntityFrameworkCore;
using TransitPass.Network.Api.Stations;

namespace TransitPass.Network.Api.Data;

/// <summary>The Network service's unit of work over network_db.</summary>
public sealed class NetworkDbContext(DbContextOptions<NetworkDbContext> options) : DbContext(options)
{
    /// <summary>The stations table. A LINQ query on this becomes SQL.</summary>
    public DbSet<Station> Stations => Set<Station>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NetworkDbContext).Assembly);
    }
}
