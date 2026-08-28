namespace HookRelay.Domain.Deliveries;

/// <summary>State of a single event heading to a single endpoint.</summary>
public enum DeliveryStatus
{
    /// <summary>Waiting for its next attempt slot. <see cref="Delivery.NextAttemptAtUtc"/> says when.</summary>
    Pending = 0,

    /// <summary>Claimed by the dispatcher and handed to Kafka. A worker owns it.</summary>
    InFlight = 1,

    /// <summary>The endpoint answered 2xx.</summary>
    Delivered = 2,

    /// <summary>The retry schedule ran out. The payload is kept and can be replayed.</summary>
    DeadLettered = 3,

    /// <summary>Abandoned because the endpoint was disabled before the delivery completed.</summary>
    Abandoned = 4,
}
