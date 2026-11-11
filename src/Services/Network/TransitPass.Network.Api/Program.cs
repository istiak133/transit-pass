using TransitPass.BuildingBlocks.Hosting;
using TransitPass.Network.Api.Data;
using TransitPass.Network.Api.Stations;

// Network service. It owns its own database (network_db) and nothing else reads it.
// Endpoints live under /api/v1/network, which is also the gateway route for this service.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgres();
builder.AddNetworkDatabase();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseServiceDefaults();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapStationEndpoints();

await app.MigrateIfEnabledAsync();

await app.RunAsync();

/// <summary>Entry point marker so integration tests can host the service via WebApplicationFactory.</summary>
public partial class Program;
