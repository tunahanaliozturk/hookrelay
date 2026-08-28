using HookRelay.Infrastructure;
using HookRelay.ServiceDefaults;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHookRelayCore(builder.Configuration);
builder.Services.AddHookRelayRelay();

IHost host = builder.Build();
await host.RunAsync();
