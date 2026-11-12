using TransitPass.BuildingBlocks.Hosting;
using TransitPass.Identity.Api.Data;
using TransitPass.Identity.Api.Sessions;
using TransitPass.Identity.Api.Users;

// Identity service. It owns its own database (identity_db) and nothing else reads it.
// Endpoints live under /api/v1/identity, which is also the gateway route for this service.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgres();
builder.AddIdentityDatabase();
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.AddSingleton<SigningKey>();
builder.Services.AddSingleton<TokenIssuer>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseServiceDefaults();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapUserEndpoints();
app.MapSessionEndpoints();

await app.MigrateIfEnabledAsync();

await app.RunAsync();

/// <summary>Entry point marker so integration tests can host the service via WebApplicationFactory.</summary>
public partial class Program;
