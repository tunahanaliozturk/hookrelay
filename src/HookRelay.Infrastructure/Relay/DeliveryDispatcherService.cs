using HookRelay.Domain.Deliveries;
using HookRelay.Domain.Endpoints;
using HookRelay.Infrastructure.Configuration;
using HookRelay.Infrastructure.Diagnostics;
using HookRelay.Infrastructure.Messaging;
using HookRelay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HookRelay.Infrastructure.Relay;

/// <summary>
/// Hands due deliveries to the workers, one per ordering key at a time.
/// </summary>
/// <remarks>
/// <para>
/// This is where the ordering guarantee is actually enforced. Kafka's per-partition ordering is not enough
/// on its own, because a delivery that fails comes back hours later on the backoff ladder and by then the
/// events behind it have long since been consumed. So a delivery is only claimed when nothing older with
/// the same ordering key is still pending or in flight. A stuck delivery holds its own stream and nothing
/// else, which is exactly the guarantee the docs promise.
/// </para>
/// <para>
/// A paused endpoint drops out of the claim, so its deliveries queue up untouched and resume in their
/// original order. Nothing is dropped and nothing is retried to exhaustion while an endpoint is paused.
/// </para>
/// </remarks>
public sealed partial class DeliveryDispatcherService(
    IServiceScopeFactory scopeFactory,
    IOptions<RelayOptions> options,
    TimeProvider timeProvider,
    ILogger<DeliveryDispatcherService> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly RelayOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<DeliveryDispatcherService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_logger, _options.DispatchBatchSize, _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            int dispatched;
            try
            {
                dispatched = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // A poller that dies on one bad batch stops the whole pipeline.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogPollFailed(_logger, exception);
                dispatched = 0;
            }

            if (dispatched == 0)
            {
                await Task.Delay(_options.PollInterval, _timeProvider, stoppingToken);
            }
        }
    }

    /// <summary>Runs one dispatch pass. Exposed so tests can drive the dispatcher deterministically.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many deliveries were published.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();
        IDeliverySignalPublisher signalPublisher = scope.ServiceProvider.GetRequiredService<IDeliverySignalPublisher>();
        HookRelayDiagnostics diagnostics = scope.ServiceProvider.GetRequiredService<HookRelayDiagnostics>();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<Delivery> claimed;

        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            claimed = await ClaimAsync(dbContext, now, cancellationToken);
            if (claimed.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return 0;
            }

            foreach (Delivery delivery in claimed)
            {
                delivery.MarkInFlight(now);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Published after the claim commits, never inside the transaction. A broker that acknowledges a
        // message the transaction then rolls back would have the workers chasing a delivery that does not
        // exist. Publishing after means the worst case is a claim with no signal, which the stale-claim
        // sweep picks up.
        int published = 0;
        foreach (Delivery delivery in claimed)
        {
            var signal = new DeliverySignal(
                delivery.Id,
                delivery.EndpointId,
                delivery.OrderingKey,
                delivery.AttemptCount + 1);

            try
            {
                await signalPublisher.PublishAsync(signal, cancellationToken);
                published++;
            }
#pragma warning disable CA1031 // One unpublishable delivery must not strand the rest of the batch.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogPublishFailed(_logger, delivery.Id, exception);
                delivery.ReleaseClaim(_timeProvider.GetUtcNow());
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        diagnostics.Dispatched.Add(published);
        return published;
    }

    private Task<List<Delivery>> ClaimAsync(
        HookRelayDbContext dbContext,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        int batchSize = _options.DispatchBatchSize;
        int pending = (int)DeliveryStatus.Pending;
        int inFlight = (int)DeliveryStatus.InFlight;
        int active = (int)EndpointStatus.Active;

        // Not composed over with LINQ on purpose: EF would wrap this in a subquery and Postgres rejects
        // FOR UPDATE there.
        return dbContext.Deliveries
            .FromSql(
                $"""
                 SELECT d.*
                 FROM deliveries AS d
                 JOIN webhook_endpoints AS e ON e.id = d.endpoint_id
                 WHERE d.status = {pending}
                   AND d.next_attempt_at_utc <= {now}
                   AND e.status = {active}
                   AND NOT EXISTS (
                       SELECT 1
                       FROM deliveries AS earlier
                       WHERE earlier.ordering_key = d.ordering_key
                         AND earlier.status IN ({pending}, {inFlight})
                         AND earlier.sequence < d.sequence)
                 ORDER BY d.sequence
                 LIMIT {batchSize}
                 FOR UPDATE OF d SKIP LOCKED
                 """)
            .ToListAsync(cancellationToken);
    }

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Delivery dispatcher started. Batch size {BatchSize}, idle poll interval {PollInterval}.")]
    private static partial void LogStarted(ILogger logger, int batchSize, TimeSpan pollInterval);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Error,
        Message = "Dispatch pass failed. Retrying on the next poll.")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Error,
        Message = "Could not publish delivery {DeliveryId}. The claim was released for another pass.")]
    private static partial void LogPublishFailed(ILogger logger, Guid deliveryId, Exception exception);
}
