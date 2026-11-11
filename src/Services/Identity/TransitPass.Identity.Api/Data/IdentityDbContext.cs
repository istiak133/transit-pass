using Microsoft.EntityFrameworkCore;
using TransitPass.Identity.Api.Users;

namespace TransitPass.Identity.Api.Data;

/// <summary>The Identity service's unit of work over identity_db.</summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    /// <summary>The users table.</summary>
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);
    }
}
