namespace HookRelay.Domain.Deliveries;

/// <summary>What happened to a delivery after a failed attempt.</summary>
public enum FailureOutcome
{
    /// <summary>Another attempt is scheduled.</summary>
    Retrying = 0,

    /// <summary>The ladder ran out. The delivery is dead-lettered and waits for a replay.</summary>
    DeadLettered = 1,
}

/// <summary>
/// One event on its way to one endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Fan-out happens when the relay turns an outbox row into deliveries, one per subscribed endpoint. From
/// that point on each delivery has its own lifecycle: its own attempt count, its own place on the backoff
/// ladder, and its own pinned secret version. One customer's broken endpoint cannot affect another's.
/// </para>
/// <para>
/// The delivery row, not a queue message, is the source of truth for state. Kafka carries the wake-up
/// signal; a lost or duplicated message costs an extra attempt at worst, because the worker re-reads the
/// row and a delivery that is already <see cref="DeliveryStatus.Delivered"/> is a no-op.
/// </para>
/// </remarks>
public sealed class Delivery
{
    private const int MaxErrorLength = 1024;

    private Delivery()
    {
        EventType = null!;
        PayloadJson = null!;
        OrderingKey = null!;
    }

    /// <summary>Time-ordered identifier. Sent to the receiver so it can de-duplicate, and the id the replay API takes.</summary>
    public Guid Id { get; private set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Destination endpoint.</summary>
    public Guid EndpointId { get; private set; }

    /// <summary>The outbox row this delivery was fanned out from.</summary>
    public Guid OutboxMessageId { get; private set; }

    /// <summary>Event name, for example invoice.paid.</summary>
    public string EventType { get; private set; }

    /// <summary>The exact body that gets signed and sent. Copied at fan-out so a later edit cannot change it.</summary>
    public string PayloadJson { get; private set; }

    /// <summary>Scopes the ordering guarantee. Also the Kafka partition key.</summary>
    public string OrderingKey { get; private set; }

    /// <summary>Secret version pinned at fan-out. Retries keep using it after a rotation.</summary>
    public int SecretVersion { get; private set; }

    /// <summary>Current state.</summary>
    public DeliveryStatus Status { get; private set; }

    /// <summary>How many HTTP attempts have been made, including attempts skipped by an open circuit.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>When the delivery was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>When the next attempt becomes due. The dispatcher polls on this.</summary>
    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    /// <summary>When the delivery reached a terminal state.</summary>
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>Why the last attempt failed. Surfaced in the delivery log.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// When the dispatcher claimed the delivery. A claim that goes stale, because the worker holding it
    /// died, is reclaimed after a timeout. Without this a crash would strand the delivery in flight forever.
    /// </summary>
    public DateTimeOffset? ClaimedAtUtc { get; private set; }

    /// <summary>How many times this delivery has been replayed out of the dead-letter store.</summary>
    public int ReplayCount { get; private set; }

    /// <summary>Creates a delivery during fan-out.</summary>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="endpointId">Destination endpoint.</param>
    /// <param name="outboxMessageId">Source outbox row.</param>
    /// <param name="eventType">Event name.</param>
    /// <param name="payloadJson">The event body.</param>
    /// <param name="orderingKey">Ordering scope.</param>
    /// <param name="secretVersion">The endpoint's secret version at fan-out time.</param>
    /// <param name="now">Current time. Also the first attempt's due time.</param>
    public static Delivery Create(
        Guid tenantId,
        Guid endpointId,
        Guid outboxMessageId,
        string eventType,
        string payloadJson,
        string orderingKey,
        int secretVersion,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderingKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(secretVersion, 1);

        return new Delivery
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            EndpointId = endpointId,
            OutboxMessageId = outboxMessageId,
            EventType = eventType,
            PayloadJson = payloadJson,
            OrderingKey = orderingKey,
            SecretVersion = secretVersion,
            Status = DeliveryStatus.Pending,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        };
    }

    /// <summary>Claims the delivery for a worker. Called by the dispatcher just before it publishes to Kafka.</summary>
    /// <param name="now">Current time. Starts the claim timeout.</param>
    public void MarkInFlight(DateTimeOffset now)
    {
        if (Status is not DeliveryStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Delivery {Id} cannot be dispatched from state {Status}.");
        }

        Status = DeliveryStatus.InFlight;
        ClaimedAtUtc = now;
    }

    /// <summary>Returns a claimed delivery to the pending pool, for example when the Kafka publish failed.</summary>
    /// <param name="now">Current time.</param>
    public void ReleaseClaim(DateTimeOffset now)
    {
        if (Status is DeliveryStatus.InFlight)
        {
            Status = DeliveryStatus.Pending;
            NextAttemptAtUtc = now;
            ClaimedAtUtc = null;
        }
    }

    /// <summary>Records a 2xx response.</summary>
    /// <param name="now">Current time.</param>
    public void RecordSuccess(DateTimeOffset now)
    {
        AttemptCount++;
        Status = DeliveryStatus.Delivered;
        CompletedAtUtc = now;
        ClaimedAtUtc = null;
        LastError = null;
    }

    /// <summary>
    /// Records a failed attempt and either books the next slot on the ladder or dead-letters the delivery.
    /// </summary>
    /// <param name="schedule">The backoff ladder.</param>
    /// <param name="jitterSample">A sample in [0, 1) used to jitter the delay.</param>
    /// <param name="error">Why the attempt failed.</param>
    /// <param name="now">Current time.</param>
    public FailureOutcome RecordFailure(
        RetrySchedule schedule,
        double jitterSample,
        string error,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        AttemptCount++;
        LastError = Truncate(error);
        ClaimedAtUtc = null;

        if (schedule.TryGetDelay(AttemptCount, jitterSample, out TimeSpan delay))
        {
            Status = DeliveryStatus.Pending;
            NextAttemptAtUtc = now + delay;
            return FailureOutcome.Retrying;
        }

        Status = DeliveryStatus.DeadLettered;
        CompletedAtUtc = now;
        return FailureOutcome.DeadLettered;
    }

    /// <summary>
    /// Dead-letters a delivery without walking the rest of the ladder, for failures that retrying cannot fix.
    /// </summary>
    /// <param name="error">Why the delivery cannot be completed.</param>
    /// <param name="now">Current time.</param>
    public void FailPermanently(string error, DateTimeOffset now)
    {
        AttemptCount++;
        LastError = Truncate(error);
        Status = DeliveryStatus.DeadLettered;
        CompletedAtUtc = now;
        ClaimedAtUtc = null;
    }

    /// <summary>Puts a dead-lettered delivery back in the queue with a fresh ladder.</summary>
    /// <param name="now">Current time.</param>
    public void Replay(DateTimeOffset now)
    {
        if (Status is not DeliveryStatus.DeadLettered)
        {
            throw new InvalidOperationException(
                $"Only dead-lettered deliveries can be replayed. Delivery {Id} is {Status}.");
        }

        Status = DeliveryStatus.Pending;
        AttemptCount = 0;
        NextAttemptAtUtc = now;
        CompletedAtUtc = null;
        ClaimedAtUtc = null;
        LastError = null;
        ReplayCount++;
    }

    /// <summary>Drops the delivery because its endpoint was retired.</summary>
    /// <param name="now">Current time.</param>
    public void Abandon(DateTimeOffset now)
    {
        Status = DeliveryStatus.Abandoned;
        CompletedAtUtc = now;
        ClaimedAtUtc = null;
    }

    private static string Truncate(string value) =>
        string.IsNullOrEmpty(value) || value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
