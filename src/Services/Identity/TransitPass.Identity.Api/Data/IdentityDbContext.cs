using Microsoft.EntityFrameworkCore;
using TransitPass.Identity.Api.Sessions;
using TransitPass.Identity.Api.Users;

namespace TransitPass.Identity.Api.Data;

/// <summary>The Identity service's unit of work over identity_db.</summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    /// <summary>The users table.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>The refresh_tokens table: SHA-256 hashes only, never a usable token.</summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);
    }
}
