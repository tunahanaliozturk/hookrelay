using System.Diagnostics;
using System.Diagnostics.Metrics;
using HookRelay.Domain.Deliveries;

namespace HookRelay.Infrastructure.Diagnostics;

/// <summary>
/// Tracing and metrics for the delivery pipeline.
/// </summary>
/// <remarks>
/// The instruments here are chosen to answer the questions an on-call engineer actually asks at 3am:
/// is the relay falling behind, is one endpoint dragging the fleet down, and how many events are sitting
/// in the dead-letter store waiting for someone to notice.
/// </remarks>
public sealed class HookRelayDiagnostics : IDisposable
{
    /// <summary>Name used for both the activity source and the meter.</summary>
    public const string SourceName = "HookRelay";

    private readonly Meter _meter;

    /// <summary>Creates the instruments.</summary>
    /// <param name="meterFactory">Factory, so tests get an isolated meter.</param>
    public HookRelayDiagnostics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(SourceName);

        Attempts = _meter.CreateCounter<long>(
            "hookrelay.delivery.attempts",
            unit: "{attempt}",
            description: "Delivery attempts, tagged by outcome.");

        AttemptLatency = _meter.CreateHistogram<double>(
            "hookrelay.delivery.attempt.duration",
            unit: "ms",
            description: "Wall-clock duration of a delivery attempt.");

        DeadLettered = _meter.CreateCounter<long>(
            "hookrelay.delivery.dead_lettered",
            unit: "{delivery}",
            description: "Deliveries that exhausted the retry ladder.");

        FannedOut = _meter.CreateCounter<long>(
            "hookrelay.outbox.fanned_out",
            unit: "{delivery}",
            description: "Deliveries created from outbox rows.");

        Dispatched = _meter.CreateCounter<long>(
            "hookrelay.delivery.dispatched",
            unit: "{delivery}",
            description: "Deliveries claimed and published to the queue.");

        ReclaimedStaleClaims = _meter.CreateCounter<long>(
            "hookrelay.delivery.stale_claims_reclaimed",
            unit: "{delivery}",
            description: "Deliveries reclaimed from a worker that stopped responding.");
    }

    /// <summary>Spans for fan-out, dispatch, and each attempt.</summary>
    public static ActivitySource ActivitySource { get; } = new(SourceName);

    /// <summary>Attempts, tagged by outcome.</summary>
    public Counter<long> Attempts { get; }

    /// <summary>Attempt duration in milliseconds.</summary>
    public Histogram<double> AttemptLatency { get; }

    /// <summary>Deliveries that ran out of attempts.</summary>
    public Counter<long> DeadLettered { get; }

    /// <summary>Deliveries created by fan-out.</summary>
    public Counter<long> FannedOut { get; }

    /// <summary>Deliveries published to the queue.</summary>
    public Counter<long> Dispatched { get; }

    /// <summary>Deliveries recovered from a stale claim.</summary>
    public Counter<long> ReclaimedStaleClaims { get; }

    /// <summary>Records one attempt.</summary>
    /// <param name="endpointId">Destination endpoint.</param>
    /// <param name="outcome">How the attempt ended.</param>
    /// <param name="latency">How long it took.</param>
    public void RecordAttempt(Guid endpointId, AttemptOutcome outcome, TimeSpan latency)
    {
        var endpointTag = new KeyValuePair<string, object?>("endpoint.id", endpointId);
        var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome.ToString());

        Attempts.Add(1, endpointTag, outcomeTag);
        AttemptLatency.Record(latency.TotalMilliseconds, endpointTag, outcomeTag);
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
