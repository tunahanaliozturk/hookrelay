using System.Globalization;
using HookRelay.ChaosReceiver;
using HookRelay.ServiceDefaults;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton<ChaosState>();
builder.Services.AddSingleton(TimeProvider.System);

WebApplication app = builder.Build();

// Starting behaviour comes from the environment, so the demo can dial in a failure rate without an
// extra configuration call.
app.Services.GetRequiredService<ChaosState>().Default = new SlotBehaviour(
    Secret: builder.Configuration["CHAOS_SECRET"],
    FailureRate: double.TryParse(
        builder.Configuration["CHAOS_FAILURE_RATE"],
        CultureInfo.InvariantCulture,
        out double rate) ? rate : 0,
    LatencyMs: int.TryParse(
        builder.Configuration["CHAOS_LATENCY_MS"],
        CultureInfo.InvariantCulture,
        out int latency) ? latency : 0);

app.MapHealthEndpoints();
app.MapChaosEndpoints();

await app.RunAsync();
