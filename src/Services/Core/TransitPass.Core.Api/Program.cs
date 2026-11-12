using TransitPass.BuildingBlocks.Auth;
using TransitPass.BuildingBlocks.Hosting;
using TransitPass.Core.Api.Data;
using TransitPass.Core.Api.Wallets;

// Transit Core service: Wallet, Ticketing and Gate. They live in one service because buying a ticket and
// exiting a gate must debit the wallet and change the ticket in ONE database transaction (ADR 0019).
// It owns its own database (core_db) and nothing else reads it.
// Endpoints live under /api/v1/wallet, /api/v1/tickets and /api/v1/gate, which are also its gateway routes.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgres();
builder.AddCoreDatabase();
builder.AddJwtAuthentication(); // trust Identity's tokens, verified locally with its public keys
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseServiceDefaults();
app.UseAuthentication(); // reads "Authorization: Bearer …" and builds the user, or leaves it anonymous
app.UseAuthorization();  // enforces RequireAuthorization: anonymous → 401

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapWalletEndpoints();

await app.MigrateIfEnabledAsync();

await app.RunAsync();

/// <summary>Entry point marker so integration tests can host the service via WebApplicationFactory.</summary>
public partial class Program;
