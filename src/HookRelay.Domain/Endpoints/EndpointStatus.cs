namespace HookRelay.Domain.Endpoints;

/// <summary>Lifecycle state of a customer-registered webhook endpoint.</summary>
public enum EndpointStatus
{
    /// <summary>Deliveries are dispatched normally.</summary>
    Active = 0,

    /// <summary>Deliveries queue up in order and resume on <see cref="WebhookEndpoint.Resume"/>. Nothing is dropped.</summary>
    Paused = 1,

    /// <summary>Terminal state. Queued deliveries are abandoned and no new ones are created.</summary>
    Disabled = 2,
}
