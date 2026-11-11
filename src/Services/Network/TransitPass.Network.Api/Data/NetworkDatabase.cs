using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace TransitPass.Network.Api.Data;

/// <summary>Wires EF Core to the service's database.</summary>
public static class NetworkDatabase
{
    /// <summary>
    /// Registers <see cref="NetworkDbContext"/> on top of the shared <see cref="NpgsqlDataSource"/>
    /// (added by AddPostgres), so EF Core and the health check use the same connection pool.
    /// </summary>
    public static WebApplicationBuilder AddNetworkDatabase(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddDbContext<NetworkDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        return builder;
    }

    /// <summary>
    /// Applies pending migrations at startup, only when <c>Database:MigrateOnStartup</c> is true.
    /// It is on in Development for convenience. In production, migrations run as a separate
    /// deploy step, so several instances never race to change the schema at once.
    /// </summary>
    public static async Task MigrateIfEnabledAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkDbContext>();
        await db.Database.MigrateAsync();
    }
}
