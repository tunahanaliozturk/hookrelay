using System.Diagnostics;
using HookRelay.Domain.Deliveries;
using HookRelay.Domain.Endpoints;
using HookRelay.Domain.Security;
using HookRelay.Infrastructure.Configuration;
using HookRelay.Infrastructure.Diagnostics;
using HookRelay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;

namespace HookRelay.Infrastructure.Sending;

/// <summary>Attempts one delivery and records what happened.</summary>
public interface IDeliveryProcessor
{
    /// <summary>Runs one attempt for a delivery. Safe to call more than once for the same id.</summary>
    /// <param name="deliveryId">Which delivery.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ProcessAsync(Guid deliveryId, CancellationToken cancellationToken);
}

/// <summary>
/// The worker-side half of the pipeline: load, sign, send, record, schedule.
/// </summary>
/// <remarks>
/// Every path through this class ends with a row written. That is what makes the delivery log complete
/// rather than a reconstruction after the fact, and it is why an open circuit is recorded as an attempt
/// with no request rather than silently skipped: a support engineer looking at the log should be able to
/// see that the fleet deliberately did not call, and why.
/// </remarks>
public sealed partial class DeliveryProcessor(
    HookRelayDbContext dbContext,
    IWebhookSender sender,
    ISecretProtector secretProtector,
    EndpointResiliencePipelines pipelines,
    HookRelayDiagnostics diagnostics,
    IOptions<DeliveryOptions> options,
    TimeProvider timeProvider,
    ILogger<DeliveryProcessor> logger) : IDeliveryProcessor
{
    private readonly HookRelayDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly IWebhookSender _sender = sender ?? throw new ArgumentNullException(nameof(sender));

    private readonly ISecretProtector _secretProtector =
        secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));

    private readonly EndpointResiliencePipelines _pipelines =
        pipelines ?? throw new ArgumentNullException(nameof(pipelines));

    private readonly HookRelayDiagnostics _diagnostics =
        diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

    private readonly RetrySchedule _schedule =
        (options ?? throw new ArgumentNullException(nameof(options))).Value.ToRetrySchedule();

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<DeliveryProcessor> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task ProcessAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        Delivery? delivery = await _dbContext.Deliveries
            .FirstOrDefaultAsync(candidate => candidate.Id == deliveryId, cancellationToken);

        if (delivery is null)
        {
            LogMissing(_logger, deliveryId);
            return;
        }

        // A duplicate signal, or one that overtook a stale-claim sweep, lands here. Both are expected.
        if (delivery.Status is not DeliveryStatus.InFlight)
        {
            LogNotInFlight(_logger, deliveryId, delivery.Status);
            return;
        }

        WebhookEndpoint? endpoint = await _dbContext.Endpoints
            .FirstOrDefaultAsync(candidate => candidate.Id == delivery.EndpointId, cancellationToken);

        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (endpoint is null || endpoint.Status is EndpointStatus.Disabled)
        {
            delivery.Abandon(now);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        if (endpoint.Status is EndpointStatus.Paused)
        {
            // Nothing is dropped. The delivery goes back to the head of its ordering key and waits there,
            // which is what keeps the resumed stream in its original order.
            delivery.ReleaseClaim(now);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!endpoint.TryGetProtectedSecret(delivery.SecretVersion, out string protectedSecret))
        {
            await FailPermanentlyAsync(delivery, endpoint, now, cancellationToken);
            return;
        }

        using Activity? activity = HookRelayDiagnostics.ActivitySource.StartActivity(
            "hookrelay.delivery.attempt",
            ActivityKind.Client);

        activity?.SetTag("endpoint.id", endpoint.Id);
        activity?.SetTag("delivery.id", delivery.Id);
        activity?.SetTag("delivery.attempt", delivery.AttemptCount + 1);
        activity?.SetTag("event.type", delivery.EventType);

        SendResult result = await AttemptAsync(endpoint, delivery, protectedSecret, cancellationToken);

        activity?.SetTag("delivery.outcome", result.Outcome.ToString());
        activity?.SetStatus(result.IsSuccess ? ActivityStatusCode.Ok : ActivityStatusCode.Error);

        now = _timeProvider.GetUtcNow();
        await RecordAsync(delivery, result, now, cancellationToken);
    }

    private async Task<SendResult> AttemptAsync(
        WebhookEndpoint endpoint,
        Delivery delivery,
        string protectedSecret,
        CancellationToken cancellationToken)
    {
        string secret = _secretProtector.Unprotect(protectedSecret);

        try
        {
            return await _pipelines.For(endpoint.Id).ExecuteAsync(
                async token => await _sender.SendAsync(endpoint, delivery, secret, token),
                cancellationToken);
        }
        catch (BrokenCircuitException)
        {
            // The point of the breaker: no socket is opened at all, so a struggling endpoint gets a rest
            // instead of a harder hammering. The attempt is still recorded and still advances the ladder,
            // which keeps the bounded retry window honest.
            LogCircuitOpen(_logger, endpoint.Id, delivery.Id);

            return new SendResult(
                AttemptOutcome.CircuitOpen,
                StatusCode: null,
                TimeSpan.Zero,
                ResponseSnippet: null,
                "Circuit open for this endpoint. No request was sent.");
        }
    }

    private async Task RecordAsync(
        Delivery delivery,
        SendResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (result.IsSuccess)
        {
            delivery.RecordSuccess(now);
        }
        else
        {
            FailureOutcome outcome = delivery.RecordFailure(
                _schedule,
                Random.Shared.NextDouble(),
                result.Error ?? result.Outcome.ToString(),
                now);

            if (outcome is FailureOutcome.DeadLettered)
            {
                _dbContext.DeadLetters.Add(
                    DeadLetter.From(delivery, result.Error ?? result.Outcome.ToString(), now));

                _diagnostics.DeadLettered.Add(1, new KeyValuePair<string, object?>("endpoint.id", delivery.EndpointId));

                LogDeadLettered(
                    _logger,
                    delivery.Id,
                    delivery.EndpointId,
                    delivery.AttemptCount,
                    result.Error ?? result.Outcome.ToString());
            }
        }

        _dbContext.DeliveryAttempts.Add(DeliveryAttempt.Record(
            delivery,
            result.Outcome,
            result.StatusCode,
            result.Latency,
            result.ResponseSnippet,
            result.Error,
            now));

        _diagnostics.RecordAttempt(delivery.EndpointId, result.Outcome, result.Latency);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task FailPermanentlyAsync(
        Delivery delivery,
        WebhookEndpoint endpoint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string Reason =
            "The signing secret this delivery was created with has been rotated out of the overlap window.";

        LogSecretVersionGone(
            _logger,
            delivery.Id,
            delivery.SecretVersion,
            endpoint.Id,
            endpoint.SecretVersion);

        delivery.FailPermanently(Reason, now);

        _dbContext.DeliveryAttempts.Add(DeliveryAttempt.Record(
            delivery,
            AttemptOutcome.BlockedByPolicy,
            statusCode: null,
            TimeSpan.Zero,
            responseSnippet: null,
            Reason,
            now));

        _dbContext.DeadLetters.Add(DeadLetter.From(delivery, Reason, now));
        _diagnostics.DeadLettered.Add(1, new KeyValuePair<string, object?>("endpoint.id", delivery.EndpointId));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(
        EventId = 1600,
        Level = LogLevel.Warning,
        Message = "Delivery {DeliveryId} no longer exists. Dropping the signal.")]
    private static partial void LogMissing(ILogger logger, Guid deliveryId);

    [LoggerMessage(
        EventId = 1601,
        Level = LogLevel.Debug,
        Message = "Delivery {DeliveryId} is {Status}, not in flight. Ignoring the signal.")]
    private static partial void LogNotInFlight(ILogger logger, Guid deliveryId, DeliveryStatus status);

    [LoggerMessage(
        EventId = 1602,
        Level = LogLevel.Information,
        Message = "Circuit open for endpoint {EndpointId}. Delivery {DeliveryId} was not attempted.")]
    private static partial void LogCircuitOpen(ILogger logger, Guid endpointId, Guid deliveryId);

    [LoggerMessage(
        EventId = 1603,
        Level = LogLevel.Warning,
        Message = "Delivery {DeliveryId} to endpoint {EndpointId} dead-lettered after {Attempts} attempts: {Reason}")]
    private static partial void LogDeadLettered(
        ILogger logger,
        Guid deliveryId,
        Guid endpointId,
        int attempts,
        string reason);

    [LoggerMessage(
        EventId = 1604,
        Level = LogLevel.Error,
        Message = "Delivery {DeliveryId} pinned secret version {PinnedVersion} but endpoint {EndpointId} is on version {CurrentVersion}.")]
    private static partial void LogSecretVersionGone(
        ILogger logger,
        Guid deliveryId,
        int pinnedVersion,
        Guid endpointId,
        int currentVersion);
}
