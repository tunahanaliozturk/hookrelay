using HookRelay.ChaosReceiver;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HookRelay.IntegrationTests;

/// <summary>
/// The flaky receiver, hosted on a real Kestrel socket for the duration of one test.
/// </summary>
/// <remarks>
/// Kestrel on a loopback port rather than <c>TestServer</c>, because the pipeline under test cares about
/// socket behaviour. A connection that is accepted and then left hanging is what the per-request timeout
/// exists for, and an in-memory transport cannot produce one.
/// </remarks>
public sealed class ChaosServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ChaosServer(WebApplication app, string baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
        State = app.Services.GetRequiredService<ChaosState>();
    }

    /// <summary>Root URL the delivery fleet posts to.</summary>
    public string BaseAddress { get; }

    /// <summary>Behaviour and recorded traffic.</summary>
    public ChaosState State { get; }

    /// <summary>Starts a receiver on a free loopback port.</summary>
    public static async Task<ChaosServer> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<ChaosState>();
        builder.Services.AddSingleton(TimeProvider.System);

        WebApplication app = builder.Build();
        app.MapChaosEndpoints();
        await app.StartAsync();

        string address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First();

        return new ChaosServer(app, address.TrimEnd('/'));
    }

    /// <summary>URL of one delivery slot.</summary>
    /// <param name="slot">Slot name.</param>
    public string SlotUrl(string slot) => $"{BaseAddress}/hooks/{slot}";

    /// <summary>Sets how a slot behaves.</summary>
    /// <param name="slot">Slot name.</param>
    /// <param name="behaviour">New behaviour.</param>
    public void Configure(string slot, SlotBehaviour behaviour) => State.Configure(slot, behaviour);

    /// <summary>Requests recorded for a slot, in arrival order.</summary>
    /// <param name="slot">Slot name.</param>
    public IReadOnlyList<ReceivedRequest> Received(string slot) => State.Received(slot);

    /// <summary>Requests the receiver answered with 2xx, in arrival order.</summary>
    /// <param name="slot">Slot name.</param>
    public IReadOnlyList<ReceivedRequest> Accepted(string slot) =>
        [.. State.Received(slot).Where(static request => request.Accepted)];

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
