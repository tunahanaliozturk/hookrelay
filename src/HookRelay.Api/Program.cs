using HookRelay.Api;
using HookRelay.Infrastructure;
using HookRelay.Infrastructure.Persistence;
using HookRelay.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHookRelayCore(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<HookRelayDbContext>("database", tags: [HostingExtensions.ReadinessTag]);

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Migrating on startup suits a single-instance demo. A real deployment runs migrations as a separate
// step so that several API instances rolling at once cannot race each other into the same lock.
if (app.Configuration.GetValue("HookRelay:MigrateOnStartup", defaultValue: false))
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<HookRelayDbContext>().Database.MigrateAsync();
}

// Served in every environment, not just development. The contract is part of what this service offers,
// and a document that only exists on a developer's machine is not a contract.
app.MapOpenApi();

app.MapHealthEndpoints();
app.MapEndpointRoutes();
app.MapDeliveryRoutes();
app.MapEventRoutes();

await app.RunAsync();

/// <summary>Entry point marker so integration tests can host this application.</summary>
public partial class Program;
