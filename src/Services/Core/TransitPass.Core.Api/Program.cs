using TransitPass.BuildingBlocks.Auth;
using TransitPass.BuildingBlocks.Hosting;
using TransitPass.Core.Api.Credentials;
using TransitPass.Core.Api.Data;
using TransitPass.Core.Api.Fares;
using TransitPass.Core.Api.Gates;
using TransitPass.Core.Api.Payments;
using TransitPass.Core.Api.Tickets;
using TransitPass.Core.Api.Topups;
using TransitPass.Core.Api.Wallets;

// Transit Core service: Wallet, Ticketing and Gate. They live in one service because buying a ticket and
// exiting a gate must debit the wallet and change the ticket in ONE database transaction (ADR 0019).
// It owns its own database (core_db) and nothing else reads it.
// Endpoints live under /api/v1/wallet, /api/v1/payments, /api/v1/tickets and /api/v1/gate, which are also its gateway routes.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgres();
builder.AddCoreDatabase();
builder.AddJwtAuthentication(); // trust Identity's tokens, verified locally with its public keys
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection(PaymentOptions.Section));
builder.Services.AddSingleton<WebhookSecret>();

// QR credentials: Core signs them with its OWN ES256 key (not Identity's), gates verify with the public half.
builder.Services.Configure<QrOptions>(builder.Configuration.GetSection(QrOptions.Section));
builder.Services.AddSingleton<QrKeys>();
builder.Services.AddSingleton<TicketCredentialIssuer>();
builder.Services.AddSingleton(TicketCredentialVerifier.Create); // the gate verifies QR codes with the public keys

// Gate devices: each proves itself with its own random key; only the keys' SHA-256 hashes are configured.
builder.Services.Configure<GateOptions>(builder.Configuration.GetSection(GateOptions.Section));
builder.Services.AddSingleton<GateDevices>();

// Core → Network over HTTP for fares. Short timeout: a purchase should fail fast with 503, not hang.
builder.Services.AddHttpClient<IFareQuotes, NetworkFareClient>(http =>
{
    http.BaseAddress = new Uri(builder.Configuration[NetworkFareClient.NetworkUrlKey] ?? "http://localhost:5102/");
    http.Timeout = NetworkFareClient.Timeout;
});
// The same typed client also answers "does this station exist?" for the gate.
builder.Services.AddTransient<IStationDirectory>(sp => (NetworkFareClient)sp.GetRequiredService<IFareQuotes>());
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
app.MapTopupEndpoints();
app.MapPaymentWebhook(); // anonymous: authenticated by its HMAC signature
app.MapTicketEndpoints();
app.MapCredentialEndpoints(); // QR credential + the public keys gates verify it with
app.MapGateEndpoints();       // gate taps: anonymous to JWT, authenticated by X-Gate-Key

_ = app.Services.GetRequiredService<WebhookSecret>(); // fail fast at startup if the secret is missing
_ = app.Services.GetRequiredService<QrKeys>();       // ...and the same for the QR signing key
_ = app.Services.GetRequiredService<GateDevices>();  // ...and for a malformed gate enrollment

await app.MigrateIfEnabledAsync();

await app.RunAsync();

/// <summary>Entry point marker so integration tests can host the service via WebApplicationFactory.</summary>
public partial class Program;
