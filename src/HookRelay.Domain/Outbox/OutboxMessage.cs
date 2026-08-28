using System.Globalization;

namespace HookRelay.Domain.Outbox;

/// <summary>How far an outbox row has got through fan-out.</summary>
public enum OutboxStatus
{
    /// <summary>Written by a business transaction, not yet fanned out.</summary>
    Pending = 0,

    /// <summary>Fanned out into deliveries and published. Terminal.</summary>
    Dispatched = 1,
}

/// <summary>
/// A domain event captured in the same database transaction as the business write that caused it.
/// </summary>
/// <remarks>
/// This row is where the durability guarantee actually starts. If the business write commits, the event
/// exists; if it rolls back, the event never existed. There is no window in which a customer was charged
/// but the event announcing it was lost, which is exactly the window a publish-after-commit call leaves open.
/// </remarks>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
        EventType = null!;
        AggregateId = null!;
        PayloadJson = null!;
        OrderingKey = null!;
    }

    /// <summary>Time-ordered identifier. Version 7 so the relay can claim rows in production order using the primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Event name, for example invoice.paid.</summary>
    public string EventType { get; private set; }

    /// <summary>Identifier of the entity the event is about, for example an invoice id.</summary>
    public string AggregateId { get; private set; }

    /// <summary>The event body, exactly as it will be signed and sent.</summary>
    public string PayloadJson { get; private set; }

    /// <summary>Scopes fan-out ordering. The relay never fans out a row while an older row with the same key is pending.</summary>
    public string OrderingKey { get; private set; }

    /// <summary>Fan-out state.</summary>
    public OutboxStatus Status { get; private set; }

    /// <summary>Number of deliveries produced by fan-out. Zero means no endpoint was subscribed.</summary>
    public int FanOutCount { get; private set; }

    /// <summary>When the business transaction wrote the row.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>When fan-out completed.</summary>
    public DateTimeOffset? DispatchedAtUtc { get; private set; }

    /// <summary>Creates an outbox row. Save it inside the caller's existing transaction, never in one of its own.</summary>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="eventType">Event name.</param>
    /// <param name="aggregateId">Identifier of the entity the event is about.</param>
    /// <param name="payloadJson">The event body.</param>
    /// <param name="now">Current time.</param>
    public static OutboxMessage Enqueue(
        Guid tenantId,
        string eventType,
        string aggregateId,
        string payloadJson,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        return new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            EventType = eventType,
            AggregateId = aggregateId,
            PayloadJson = payloadJson,
            OrderingKey = string.Create(CultureInfo.InvariantCulture, $"{tenantId:N}|{aggregateId}"),
            Status = OutboxStatus.Pending,
            CreatedAtUtc = now,
        };
    }

    /// <summary>Marks the row fanned out.</summary>
    /// <param name="fanOutCount">How many deliveries were created.</param>
    /// <param name="now">Current time.</param>
    public void MarkDispatched(int fanOutCount, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fanOutCount);

        Status = OutboxStatus.Dispatched;
        FanOutCount = fanOutCount;
        DispatchedAtUtc = now;
    }
}
