using HookRelay.Domain.Deliveries;
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
/// Two housekeeping jobs the pipeline cannot run without: reclaiming stale claims and enforcing retention.
/// </summary>
/// <remarks>
/// The stale-claim sweep is the piece that makes a worker crash survivable. A worker that dies between
/// claiming a delivery and recording the attempt leaves the row in flight with nobody working on it, and
/// without this sweep that delivery would sit there forever. Reclaiming after a timeout costs at worst one
/// duplicate HTTP call, which is the trade at-least-once already makes.
/// </remarks>
public sealed partial class MaintenanceService(
    IServiceScopeFactory scopeFactory,
    IOptions<RelayOptions> relayOptions,
    IOptions<DeliveryOptions> deliveryOptions,
    TimeProvider timeProvider,
    ILogger<MaintenanceService> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly RelayOptions _relayOptions =
        relayOptions?.Value ?? throw new ArgumentNullException(nameof(relayOptions));

    private readonly DeliveryOptions _deliveryOptions =
        deliveryOptions?.Value ?? throw new ArgumentNullException(nameof(deliveryOptions));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<MaintenanceService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset nextRetentionSweep = _timeProvider.GetUtcNow() + _relayOptions.RetentionSweepInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReclaimStaleClaimsAsync(stoppingToken);

                if (_relayOptions.RetentionSweepInterval > TimeSpan.Zero
                    && _timeProvider.GetUtcNow() >= nextRetentionSweep)
                {
                    await PurgeExpiredAsync(stoppingToken);
                    nextRetentionSweep = _timeProvider.GetUtcNow() + _relayOptions.RetentionSweepInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Housekeeping must not take the host down.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogSweepFailed(_logger, exception);
            }

            await Task.Delay(_relayOptions.StaleClaimSweepInterval, _timeProvider, stoppingToken);
        }
    }

    /// <summary>Returns deliveries whose worker went away to the pending pool. Exposed for tests.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many deliveries were reclaimed.</returns>
    public async Task<int> ReclaimStaleClaimsAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();
        HookRelayDiagnostics diagnostics = scope.ServiceProvider.GetRequiredService<HookRelayDiagnostics>();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset cutoff = now - _deliveryOptions.StaleClaimTimeout;

        int reclaimed = await dbContext.Deliveries
            .Where(delivery => delivery.Status == DeliveryStatus.InFlight
                && delivery.ClaimedAtUtc != null
                && delivery.ClaimedAtUtc < cutoff)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(delivery => delivery.Status, DeliveryStatus.Pending)
                    .SetProperty(delivery => delivery.NextAttemptAtUtc, now)
                    .SetProperty(delivery => delivery.ClaimedAtUtc, (DateTimeOffset?)null),
                cancellationToken);

        if (reclaimed > 0)
        {
            diagnostics.ReclaimedStaleClaims.Add(reclaimed);
            LogReclaimed(_logger, reclaimed, _deliveryOptions.StaleClaimTimeout);
        }

        return reclaimed;
    }

    /// <summary>Deletes rows past their retention window. Exposed for tests.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset attemptCutoff = now - _relayOptions.AttemptRetention;
        DateTimeOffset deadLetterCutoff = now - _relayOptions.DeadLetterRetention;

        int attempts = await dbContext.DeliveryAttempts
            .Where(attempt => attempt.AttemptedAtUtc < attemptCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        // Only replayed dead letters are swept. An unreplayed one still needs a human, however old it is.
        int deadLetters = await dbContext.DeadLetters
            .Where(deadLetter => deadLetter.DeadLetteredAtUtc < deadLetterCutoff
                && deadLetter.ReplayedAtUtc != null)
            .ExecuteDeleteAsync(cancellationToken);

        int outbox = await dbContext.OutboxMessages
            .Where(message => message.DispatchedAtUtc != null && message.DispatchedAtUtc < attemptCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (attempts + deadLetters + outbox > 0)
        {
            LogPurged(_logger, attempts, deadLetters, outbox);
        }
    }

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Warning,
        Message = "Reclaimed {Count} deliveries whose claim went stale after {Timeout}.")]
    private static partial void LogReclaimed(ILogger logger, int count, TimeSpan timeout);

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Information,
        Message = "Retention sweep removed {Attempts} attempts, {DeadLetters} dead letters, {Outbox} outbox rows.")]
    private static partial void LogPurged(ILogger logger, int attempts, int deadLetters, int outbox);

    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Error,
        Message = "Maintenance sweep failed. Retrying on the next interval.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
