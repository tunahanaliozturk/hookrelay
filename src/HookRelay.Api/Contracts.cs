using System.ComponentModel.DataAnnotations;
using HookRelay.Domain.Deliveries;
using HookRelay.Domain.Endpoints;

namespace HookRelay.Api;

/// <summary>Request to register a webhook endpoint.</summary>
/// <param name="Url">Destination URL. Must be https unless the host is configured for local development.</param>
/// <param name="Description">Label shown in the delivery log.</param>
/// <param name="EventTypes">Event types to subscribe to. Supports exact names and prefix wildcards.</param>
/// <param name="OrderingStrategy">How wide the ordered stream should be.</param>
public sealed record RegisterEndpointRequest(
    [property: Required, MaxLength(2048)] string Url,
    [property: MaxLength(256)] string? Description,
    [property: Required, MinLength(1)] IReadOnlyList<string> EventTypes,
    OrderingStrategy OrderingStrategy = OrderingStrategy.PerEndpoint);

/// <summary>Request to replace an endpoint's subscriptions.</summary>
/// <param name="EventTypes">The new list.</param>
public sealed record UpdateSubscriptionsRequest(
    [property: Required, MinLength(1)] IReadOnlyList<string> EventTypes);

/// <summary>Request to publish a domain event.</summary>
/// <param name="EventType">Event name, for example invoice.paid.</param>
/// <param name="AggregateId">Identifier of the entity the event is about.</param>
/// <param name="Payload">Event body. Delivered verbatim.</param>
public sealed record PublishEventRequest(
    [property: Required, MaxLength(128)] string EventType,
    [property: Required, MaxLength(128)] string AggregateId,
    [property: Required] System.Text.Json.JsonElement Payload);

/// <summary>An endpoint as returned by the API. The signing secret is never part of this.</summary>
/// <param name="Id">Endpoint id.</param>
/// <param name="Url">Destination URL.</param>
/// <param name="Description">Label.</param>
/// <param name="EventTypes">Current subscriptions.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="OrderingStrategy">Ordering scope.</param>
/// <param name="SecretVersion">Current secret version.</param>
/// <param name="CreatedAtUtc">Creation time.</param>
/// <param name="SecretRotatedAtUtc">Last rotation, if any.</param>
public sealed record EndpointResponse(
    Guid Id,
    string Url,
    string Description,
    IReadOnlyList<string> EventTypes,
    EndpointStatus Status,
    OrderingStrategy OrderingStrategy,
    int SecretVersion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SecretRotatedAtUtc)
{
    /// <summary>Projects an entity.</summary>
    /// <param name="endpoint">The entity.</param>
    public static EndpointResponse From(WebhookEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return new EndpointResponse(
            endpoint.Id,
            endpoint.Url,
            endpoint.Description,
            endpoint.SubscribedEventTypes,
            endpoint.Status,
            endpoint.OrderingStrategy,
            endpoint.SecretVersion,
            endpoint.CreatedAtUtc,
            endpoint.SecretRotatedAtUtc);
    }
}

/// <summary>
/// The one response that carries a signing secret.
/// </summary>
/// <remarks>
/// Returned by registration and by rotation, and by nothing else. The stored copy is encrypted and no read
/// path decrypts it, so a customer who loses this value rotates rather than asking support to look it up.
/// </remarks>
/// <param name="Endpoint">The endpoint.</param>
/// <param name="Secret">The signing secret, shown this once.</param>
public sealed record EndpointWithSecretResponse(EndpointResponse Endpoint, string Secret);

/// <summary>A delivery in the customer-facing log.</summary>
/// <param name="Id">Delivery id. Also the value sent in the delivery id header.</param>
/// <param name="EndpointId">Destination endpoint.</param>
/// <param name="EventType">Event name.</param>
/// <param name="Status">Current state.</param>
/// <param name="AttemptCount">Attempts made so far.</param>
/// <param name="CreatedAtUtc">When the delivery was created.</param>
/// <param name="NextAttemptAtUtc">When the next attempt is due.</param>
/// <param name="CompletedAtUtc">When it reached a terminal state.</param>
/// <param name="LastError">Why the last attempt failed.</param>
public sealed record DeliveryResponse(
    Guid Id,
    Guid EndpointId,
    string EventType,
    DeliveryStatus Status,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset NextAttemptAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? LastError);

/// <summary>One recorded attempt.</summary>
/// <param name="AttemptNumber">1-based attempt number.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="StatusCode">Response status, when there was one.</param>
/// <param name="LatencyMs">Request duration.</param>
/// <param name="ResponseSnippet">Start of the response body.</param>
/// <param name="Error">Failure message.</param>
/// <param name="AttemptedAtUtc">When it started.</param>
/// <param name="NextAttemptAtUtc">When the following attempt is due.</param>
public sealed record AttemptResponse(
    int AttemptNumber,
    AttemptOutcome Outcome,
    int? StatusCode,
    int LatencyMs,
    string? ResponseSnippet,
    string? Error,
    DateTimeOffset AttemptedAtUtc,
    DateTimeOffset? NextAttemptAtUtc);

/// <summary>A delivery together with its full attempt history.</summary>
/// <param name="Delivery">The delivery.</param>
/// <param name="Attempts">Every attempt, oldest first.</param>
public sealed record DeliveryDetailResponse(
    DeliveryResponse Delivery,
    IReadOnlyList<AttemptResponse> Attempts);

/// <summary>A dead-lettered delivery.</summary>
/// <param name="Id">Dead-letter id.</param>
/// <param name="DeliveryId">The delivery that failed.</param>
/// <param name="EventType">Event name.</param>
/// <param name="FailureReason">Why the final attempt failed.</param>
/// <param name="AttemptCount">Attempts made before giving up.</param>
/// <param name="DeadLetteredAtUtc">When it was dead-lettered.</param>
/// <param name="ReplayedAtUtc">When it was last replayed.</param>
/// <param name="ReplayCount">How many times it has been replayed.</param>
public sealed record DeadLetterResponse(
    Guid Id,
    Guid DeliveryId,
    string EventType,
    string FailureReason,
    int AttemptCount,
    DateTimeOffset DeadLetteredAtUtc,
    DateTimeOffset? ReplayedAtUtc,
    int ReplayCount);

/// <summary>Result of a bulk replay.</summary>
/// <param name="Replayed">How many deliveries were requeued.</param>
public sealed record BulkReplayResponse(int Replayed);

/// <summary>Result of publishing an event.</summary>
/// <param name="OutboxMessageId">The queued outbox row.</param>
public sealed record PublishEventResponse(Guid OutboxMessageId);
