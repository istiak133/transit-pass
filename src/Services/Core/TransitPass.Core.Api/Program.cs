using TransitPass.BuildingBlocks.Hosting;

// Transit Core service: Wallet, Ticketing and Gate. They live in one service because buying a ticket and
// exiting a gate must debit the wallet and change the ticket in ONE database transaction (ADR 0019).
// It owns its own database (core_db) and nothing else reads it.
// Endpoints live under /api/v1/wallet, /api/v1/tickets and /api/v1/gate, which are also its gateway routes.
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
