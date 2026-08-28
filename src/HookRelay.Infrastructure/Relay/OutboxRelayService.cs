using HookRelay.Domain.Deliveries;
using HookRelay.Domain.Endpoints;
using HookRelay.Domain.Outbox;
using HookRelay.Infrastructure.Configuration;
using HookRelay.Infrastructure.Diagnostics;
using HookRelay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HookRelay.Infrastructure.Relay;

/// <summary>
/// Turns outbox rows into deliveries, one per subscribed endpoint.
/// </summary>
/// <remarks>
/// <para>
/// The claim query is the interesting part. A plain <c>FOR UPDATE SKIP LOCKED</c> poll lets two relay
/// instances pick up two rows for the same aggregate at the same time and fan them out in whichever order
/// they happen to finish, which quietly breaks ordering long before anyone notices. Adding a not-exists
/// probe for an older pending row with the same ordering key means each key only ever has one row in flight,
/// so order survives however many relay instances are running.
/// </para>
/// <para>
/// The cost is that one key advances one row per pass. Rows for different keys still go out in parallel,
/// and a pass that found work loops straight round instead of sleeping, so throughput across many keys is
/// unaffected. A single key that needs more than that wants a finer ordering strategy, not a faster relay.
/// </para>
/// </remarks>
public sealed partial class OutboxRelayService(
    IServiceScopeFactory scopeFactory,
    IOptions<RelayOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRelayService> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly RelayOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<OutboxRelayService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_logger, _options.OutboxBatchSize, _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            int fannedOut;
            try
            {
                fannedOut = await RunOnceAsync(stoppingToken);
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
                fannedOut = 0;
            }

            if (fannedOut == 0)
            {
                await Task.Delay(_options.PollInterval, _timeProvider, stoppingToken);
            }
        }
    }

    /// <summary>Runs one fan-out pass. Exposed so tests can drive the relay deterministically.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many deliveries were created.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();
        HookRelayDiagnostics diagnostics = scope.ServiceProvider.GetRequiredService<HookRelayDiagnostics>();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        List<OutboxMessage> claimed = await ClaimAsync(dbContext, cancellationToken);
        if (claimed.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        Guid[] tenantIds = [.. claimed.Select(static message => message.TenantId).Distinct()];

        List<WebhookEndpoint> endpoints = await dbContext.Endpoints
            .Where(endpoint => tenantIds.Contains(endpoint.TenantId)
                && endpoint.Status != EndpointStatus.Disabled)
            .ToListAsync(cancellationToken);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        int created = 0;

        foreach (OutboxMessage message in claimed)
        {
            int fanOut = 0;

            foreach (WebhookEndpoint endpoint in endpoints)
            {
                if (endpoint.TenantId != message.TenantId || !endpoint.IsSubscribedTo(message.EventType))
                {
                    continue;
                }

                dbContext.Deliveries.Add(Delivery.Create(
                    message.TenantId,
                    endpoint.Id,
                    message.Id,
                    message.Sequence,
                    message.EventType,
                    message.PayloadJson,
                    OrderingKey.For(endpoint.Id, message.AggregateId, endpoint.OrderingStrategy),
                    endpoint.SecretVersion,
                    now));

                fanOut++;
            }

            message.MarkDispatched(fanOut, now);
            created += fanOut;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        diagnostics.FannedOut.Add(created);
        LogFannedOut(_logger, claimed.Count, created);

        return created;
    }

    private Task<List<OutboxMessage>> ClaimAsync(
        HookRelayDbContext dbContext,
        CancellationToken cancellationToken)
    {
        int batchSize = _options.OutboxBatchSize;
        int pending = (int)OutboxStatus.Pending;

        // Not composed over with LINQ on purpose: EF would wrap this in a subquery and Postgres rejects
        // FOR UPDATE there.
        return dbContext.OutboxMessages
            .FromSql(
                $"""
                 SELECT o.*
                 FROM outbox_messages AS o
                 WHERE o.status = {pending}
                   AND NOT EXISTS (
                       SELECT 1
                       FROM outbox_messages AS earlier
                       WHERE earlier.ordering_key = o.ordering_key
                         AND earlier.status = {pending}
                         AND earlier.sequence < o.sequence)
                 ORDER BY o.sequence
                 LIMIT {batchSize}
                 FOR UPDATE SKIP LOCKED
                 """)
            .ToListAsync(cancellationToken);
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Outbox relay started. Batch size {BatchSize}, idle poll interval {PollInterval}.")]
    private static partial void LogStarted(ILogger logger, int batchSize, TimeSpan pollInterval);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Debug,
        Message = "Fanned out {MessageCount} outbox rows into {DeliveryCount} deliveries.")]
    private static partial void LogFannedOut(ILogger logger, int messageCount, int deliveryCount);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Outbox fan-out pass failed. Retrying on the next poll.")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);
}
