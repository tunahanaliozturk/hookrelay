namespace HookRelay.Domain.Deliveries;

/// <summary>
/// A delivery that used up its retry ladder without ever getting a 2xx.
/// </summary>
/// <remarks>
/// The payload is kept here rather than referenced, so a replay does not depend on the originating outbox
/// row still being around after retention has swept it. Replaying is deliberately a separate operator
/// action instead of an endless retry loop: at some point the honest answer is that the endpoint is broken
/// and a human should look at it.
/// </remarks>
public sealed class DeadLetter
{
    private DeadLetter()
    {
        EventType = null!;
        PayloadJson = null!;
        FailureReason = null!;
    }

    /// <summary>Identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Destination endpoint.</summary>
    public Guid EndpointId { get; private set; }

    /// <summary>The delivery that failed. Replay works through this id.</summary>
    public Guid DeliveryId { get; private set; }

    /// <summary>The outbox row it came from, for tracing back to the business write.</summary>
    public Guid OutboxMessageId { get; private set; }

    /// <summary>Event name.</summary>
    public string EventType { get; private set; }

    /// <summary>The event body, kept so replay does not depend on the outbox row surviving retention.</summary>
    public string PayloadJson { get; private set; }

    /// <summary>Why the final attempt failed.</summary>
    public string FailureReason { get; private set; }

    /// <summary>How many attempts were made before giving up.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>When the delivery was dead-lettered.</summary>
    public DateTimeOffset DeadLetteredAtUtc { get; private set; }

    /// <summary>When it was last replayed, if it ever was.</summary>
    public DateTimeOffset? ReplayedAtUtc { get; private set; }

    /// <summary>How many times an operator has replayed it.</summary>
    public int ReplayCount { get; private set; }

    /// <summary>Creates a dead-letter record for an exhausted delivery.</summary>
    /// <param name="delivery">The exhausted delivery.</param>
    /// <param name="failureReason">Why the final attempt failed.</param>
    /// <param name="now">Current time.</param>
    public static DeadLetter From(Delivery delivery, string failureReason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        return new DeadLetter
        {
            Id = Guid.CreateVersion7(now),
            TenantId = delivery.TenantId,
            EndpointId = delivery.EndpointId,
            DeliveryId = delivery.Id,
            OutboxMessageId = delivery.OutboxMessageId,
            EventType = delivery.EventType,
            PayloadJson = delivery.PayloadJson,
            FailureReason = string.IsNullOrWhiteSpace(failureReason) ? "unknown" : failureReason,
            AttemptCount = delivery.AttemptCount,
            DeadLetteredAtUtc = now,
        };
    }

    /// <summary>Marks the record as replayed. The row is kept, so the full history survives the replay.</summary>
    /// <param name="now">Current time.</param>
    public void MarkReplayed(DateTimeOffset now)
    {
        ReplayedAtUtc = now;
        ReplayCount++;
    }
}
