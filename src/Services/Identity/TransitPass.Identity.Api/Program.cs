using TransitPass.BuildingBlocks.Hosting;

// Identity service. It owns its own database (identity_db) and nothing else reads it.
// Endpoints live under /api/v1/identity, which is also the gateway route for this service.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgres();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseServiceDefaults();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await app.RunAsync();

/// <summary>Entry point marker so integration tests can host the service via WebApplicationFactory.</summary>
public partial class Program;
