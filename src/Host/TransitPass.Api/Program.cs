var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Liveness only says "the process is up". Readiness (dependencies) is added with the first dependency.
app.MapHealthChecks("/health/live");

await app.RunAsync();

/// <summary>Entry point marker so integration tests can host the app via WebApplicationFactory.</summary>
public partial class Program;
