using System.Collections.Concurrent;
using System.Globalization;
using HookRelay.ChaosReceiver;
using HookRelay.Domain.Deliveries;
using HookRelay.Domain.Endpoints;
using HookRelay.Domain.Security;
using HookRelay.Infrastructure;
using HookRelay.Infrastructure.Messaging;
using HookRelay.Infrastructure.Outbox;
using HookRelay.Infrastructure.Persistence;
using HookRelay.Infrastructure.Relay;
using HookRelay.Infrastructure.Security;
using HookRelay.Infrastructure.Sending;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HookRelay.IntegrationTests;

/// <summary>How much of the stack the harness runs.</summary>
public enum HarnessMode
{
    /// <summary>
    /// Relay, dispatcher and worker are driven by hand and the queue is replaced by an in-memory recorder.
    /// Deterministic, no broker, and the clock can be moved forward instead of waited out.
    /// </summary>
    Stepped,

    /// <summary>
    /// The real thing: background pollers running on their own, signals travelling through Kafka, and a
    /// system clock. Slower, and the only way to prove the wiring actually works.
    /// </summary>
    Live,
}

/// <summary>Signals the dispatcher published, kept in memory for stepped runs.</summary>
internal sealed class RecordingSignalPublisher : IDeliverySignalPublisher
{
    private readonly ConcurrentQueue<DeliverySignal> _signals = new();

    public Task PublishAsync(DeliverySignal signal, CancellationToken cancellationToken)
    {
        _signals.Enqueue(signal);
        return Task.CompletedTask;
    }

    public bool TryDequeue(out DeliverySignal signal) => _signals.TryDequeue(out signal);
}

/// <summary>A complete pipeline wired against the shared containers, scoped to one test.</summary>
public sealed class PipelineHarness : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly RecordingSignalPublisher? _recorder;

    private PipelineHarness(
        IHost host,
        ChaosServer chaos,
        TestTimeProvider? clock,
        RecordingSignalPublisher? recorder)
    {
        _host = host;
        _recorder = recorder;
        Chaos = chaos;
        Clock = clock;
    }

    /// <summary>The flaky receiver deliveries are aimed at.</summary>
    public ChaosServer Chaos { get; }

    /// <summary>The hand-moved clock, in stepped mode.</summary>
    public TestTimeProvider? Clock { get; }

    /// <summary>Tenant every helper on this harness acts for.</summary>
    public Guid TenantId { get; } = Guid.NewGuid();

    /// <summary>Application services.</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>Starts a harness with its own database, topic, and receiver.</summary>
    /// <param name="containers">Shared containers.</param>
    /// <param name="name">Unique name for this run's database and topic.</param>
    /// <param name="mode">How much of the stack to run.</param>
    /// <param name="settings">Extra configuration, applied last.</param>
    public static async Task<PipelineHarness> StartAsync(
        ContainerFixture containers,
        string name,
        HarnessMode mode = HarnessMode.Stepped,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(containers);

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string connectionString = await containers.CreateDatabaseAsync($"{name}_{suffix}");

        TestTimeProvider? clock = mode is HarnessMode.Stepped
            ? new TestTimeProvider(DateTimeOffset.UtcNow)
            : null;

        ChaosServer chaos = await ChaosServer.StartAsync();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        Dictionary<string, string?> configuration = new(StringComparer.Ordinal)
        {
            ["ConnectionStrings:hookrelay"] = connectionString,
            ["HookRelay:Kafka:BootstrapServers"] = containers.KafkaBootstrapServers,
            ["HookRelay:Kafka:Topic"] = $"hookrelay.{name}.{suffix}",
            ["HookRelay:Kafka:ConsumerGroup"] = $"workers.{suffix}",
            ["HookRelay:Kafka:PartitionCount"] = "4",
            ["HookRelay:SecretProtection:Key"] = AesGcmSecretProtector.GenerateKey(),

            // A compressed ladder. Same shape as production, four orders of magnitude shorter, so the whole
            // retry cycle including exhaustion finishes inside a test rather than a day and a half.
            ["HookRelay:Delivery:RetryDelays:0"] = "00:00:00.200",
            ["HookRelay:Delivery:RetryDelays:1"] = "00:00:00.400",
            ["HookRelay:Delivery:RetryDelays:2"] = "00:00:00.800",
            ["HookRelay:Delivery:JitterRatio"] = "0",
            ["HookRelay:Delivery:RequestTimeout"] = "00:00:02",
            ["HookRelay:Delivery:StaleClaimTimeout"] = "00:00:30",
            ["HookRelay:Delivery:CircuitMinimumThroughput"] = "3",
            ["HookRelay:Delivery:CircuitSamplingDuration"] = "00:00:10",
            ["HookRelay:Delivery:CircuitBreakDuration"] = "00:00:05",
            ["HookRelay:Delivery:AllowInsecureHttp"] = "true",
            ["HookRelay:Delivery:AllowPrivateNetworkDestinations"] = "true",
            ["HookRelay:Relay:PollInterval"] = "00:00:00.050",
            ["HookRelay:Relay:StaleClaimSweepInterval"] = "00:00:01",
            ["HookRelay:Relay:RetentionSweepInterval"] = "00:00:00",
        };

        if (settings is not null)
        {
            foreach ((string key, string? value) in settings)
            {
                configuration[key] = value;
            }
        }

        builder.Configuration.AddInMemoryCollection(configuration);

        if (clock is not null)
        {
            builder.Services.AddSingleton<TimeProvider>(clock);
        }

        builder.Services.AddHookRelayCore(builder.Configuration);

        RecordingSignalPublisher? recorder = null;

        if (mode is HarnessMode.Stepped)
        {
            recorder = new RecordingSignalPublisher();
            builder.Services.AddSingleton<IDeliverySignalPublisher>(recorder);

            // Registered as plain singletons rather than hosted services. The test decides when a pass runs.
            builder.Services.AddSingleton<OutboxRelayService>();
            builder.Services.AddSingleton<DeliveryDispatcherService>();
            builder.Services.AddSingleton<MaintenanceService>();
        }
        else
        {
            builder.Services.AddHookRelayRelay();
            builder.Services.AddHookRelayWorker();
        }

        IHost host = builder.Build();

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HookRelayDbContext>().Database.MigrateAsync();
        }

        await host.StartAsync();

        return new PipelineHarness(host, chaos, clock, recorder);
    }

    /// <summary>Opens a scoped database context.</summary>
    public AsyncServiceScope CreateScope() => _host.Services.CreateAsyncScope();

    /// <summary>Registers an endpoint pointing at one receiver slot.</summary>
    /// <param name="slot">Receiver slot.</param>
    /// <param name="eventTypes">Subscriptions.</param>
    /// <param name="strategy">Ordering scope.</param>
    /// <param name="verifySignature">Whether the receiver should verify the signature it is sent.</param>
    /// <param name="failureRate">Probability the slot answers with 500.</param>
    /// <param name="latencyMs">Artificial delay the slot adds.</param>
    public async Task<(WebhookEndpoint Endpoint, string Secret)> RegisterEndpointAsync(
        string slot,
        string[]? eventTypes = null,
        OrderingStrategy strategy = OrderingStrategy.PerEndpoint,
        bool verifySignature = true,
        double failureRate = 0,
        int latencyMs = 0)
    {
        await using AsyncServiceScope scope = CreateScope();
        HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();
        ISecretProtector protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        TimeProvider time = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        string secret = WebhookSecret.Generate();

        WebhookEndpoint endpoint = WebhookEndpoint.Register(
            TenantId,
            new Uri(Chaos.SlotUrl(slot)),
            slot,
            eventTypes ?? ["*"],
            protector.Protect(secret),
            strategy,
            time.GetUtcNow());

        dbContext.Endpoints.Add(endpoint);
        await dbContext.SaveChangesAsync();

        Chaos.Configure(slot, new SlotBehaviour(
            Secret: verifySignature ? secret : null,
            FailureRate: failureRate,
            LatencyMs: latencyMs));

        return (endpoint, secret);
    }

    /// <summary>Writes an outbox row, the way a producing service would.</summary>
    /// <param name="eventType">Event name.</param>
    /// <param name="aggregateId">Identifier of the entity the event is about.</param>
    /// <param name="sequence">Sequence number written into the payload, so ordering can be asserted.</param>
    public async Task<Guid> PublishAsync(string eventType, string aggregateId, int sequence)
    {
        await using AsyncServiceScope scope = CreateScope();
        HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();
        IWebhookEventPublisher publisher = scope.ServiceProvider.GetRequiredService<IWebhookEventPublisher>();

        Guid id = publisher.PublishJson(
            TenantId,
            eventType,
            aggregateId,
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"aggregateId":"{{aggregateId}}","sequence":{{sequence}}}""")).Id;

        await dbContext.SaveChangesAsync();
        return id;
    }

    /// <summary>Runs one fan-out, one dispatch, and every delivery the dispatch produced. Stepped mode only.</summary>
    /// <returns>How many deliveries were attempted.</returns>
    public async Task<int> StepAsync()
    {
        RecordingSignalPublisher recorder = _recorder
            ?? throw new InvalidOperationException("Stepping is only available in stepped mode.");

        await _host.Services.GetRequiredService<OutboxRelayService>().RunOnceAsync(CancellationToken.None);
        await _host.Services.GetRequiredService<DeliveryDispatcherService>().RunOnceAsync(CancellationToken.None);

        int attempted = 0;
        while (recorder.TryDequeue(out DeliverySignal signal))
        {
            await using AsyncServiceScope scope = CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<IDeliveryProcessor>()
                .ProcessAsync(signal.DeliveryId, CancellationToken.None);
            attempted++;
        }

        return attempted;
    }

    /// <summary>Steps until nothing is left to do, moving the clock forward between passes.</summary>
    /// <param name="advance">How far to move the clock when a pass finds no work.</param>
    /// <param name="maxRounds">Safety valve.</param>
    public async Task DrainAsync(TimeSpan? advance = null, int maxRounds = 250)
    {
        TimeSpan step = advance ?? TimeSpan.FromSeconds(1);

        for (int round = 0; round < maxRounds; round++)
        {
            if (await StepAsync() == 0)
            {
                if (!await HasPendingWorkAsync())
                {
                    return;
                }

                Clock?.Advance(step);
            }
        }
    }

    /// <summary>
    /// Throws away signals the dispatcher published without processing them, which is what a worker that
    /// died between claiming a delivery and attempting it leaves behind.
    /// </summary>
    /// <returns>How many signals were discarded.</returns>
    public int DiscardPendingSignals()
    {
        RecordingSignalPublisher recorder = _recorder
            ?? throw new InvalidOperationException("Discarding signals is only available in stepped mode.");

        int discarded = 0;
        while (recorder.TryDequeue(out _))
        {
            discarded++;
        }

        return discarded;
    }

    /// <summary>Runs the stale-claim sweep. Stepped mode only.</summary>
    public Task<int> SweepStaleClaimsAsync() =>
        _host.Services.GetRequiredService<MaintenanceService>().ReclaimStaleClaimsAsync(CancellationToken.None);

    /// <summary>How many deliveries exist, whatever their state.</summary>
    public async Task<int> DeliveryCountAsync()
    {
        await using AsyncServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<HookRelayDbContext>().Deliveries.CountAsync();
    }

    /// <summary>
    /// True once <paramref name="expected"/> deliveries exist and none of them are still moving.
    /// </summary>
    /// <remarks>
    /// Waiting on "nothing pending" alone is a race in live mode: right after publishing, the relay has
    /// not fanned anything out yet, so there is nothing pending and the wait returns immediately.
    /// </remarks>
    /// <param name="expected">How many deliveries the publishes should produce.</param>
    public async Task<bool> IsSettledAsync(int expected) =>
        await DeliveryCountAsync() >= expected && !await HasPendingWorkAsync();

    /// <summary>True while any delivery is still pending or in flight.</summary>
    public async Task<bool> HasPendingWorkAsync()
    {
        await using AsyncServiceScope scope = CreateScope();
        HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();

        return await dbContext.Deliveries.AnyAsync(delivery =>
            delivery.Status == DeliveryStatus.Pending || delivery.Status == DeliveryStatus.InFlight);
    }

    /// <summary>Waits for a condition, polling until it holds or the timeout expires.</summary>
    /// <param name="condition">What to wait for.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="description">Included in the failure message.</param>
    public static async Task WaitForAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        string description)
    {
        ArgumentNullException.ThrowIfNull(condition);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out after {timeout} waiting for {description}.");
    }

    /// <summary>Reads the deliveries for one endpoint, oldest first.</summary>
    /// <param name="endpointId">Endpoint.</param>
    public async Task<List<Delivery>> DeliveriesAsync(Guid endpointId)
    {
        await using AsyncServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<HookRelayDbContext>()
            .Deliveries
            .AsNoTracking()
            .Where(delivery => delivery.EndpointId == endpointId)
            .OrderBy(delivery => delivery.Id)
            .ToListAsync();
    }

    /// <summary>Reads the attempt history for one delivery, oldest first.</summary>
    /// <param name="deliveryId">Delivery.</param>
    public async Task<List<DeliveryAttempt>> AttemptsAsync(Guid deliveryId)
    {
        await using AsyncServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<HookRelayDbContext>()
            .DeliveryAttempts
            .AsNoTracking()
            .Where(attempt => attempt.DeliveryId == deliveryId)
            .OrderBy(attempt => attempt.AttemptNumber)
            .ToListAsync();
    }

    /// <summary>Reads the dead letters for one endpoint.</summary>
    /// <param name="endpointId">Endpoint.</param>
    public async Task<List<DeadLetter>> DeadLettersAsync(Guid endpointId)
    {
        await using AsyncServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<HookRelayDbContext>()
            .DeadLetters
            .AsNoTracking()
            .Where(deadLetter => deadLetter.EndpointId == endpointId)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(10));
        _host.Dispose();
        await Chaos.DisposeAsync();
    }
}
