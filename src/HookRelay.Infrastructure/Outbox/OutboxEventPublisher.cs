using System.Text.Json;
using HookRelay.Domain.Outbox;
using HookRelay.Infrastructure.Persistence;

namespace HookRelay.Infrastructure.Outbox;

/// <summary>
/// How a producing service hands an event to the delivery pipeline.
/// </summary>
/// <remarks>
/// The whole integration is one call inside a transaction the caller already has open. No retry policy to
/// configure, no broker to reach, nothing to reason about if the business write rolls back: the event row
/// rolls back with it. Producers should never have to think about signing, backoff, or which customers are
/// subscribed, and with this interface they do not.
/// </remarks>
public interface IWebhookEventPublisher
{
    /// <summary>Queues an event. Takes effect when the caller saves and commits, and not before.</summary>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="eventType">Event name, for example invoice.paid.</param>
    /// <param name="aggregateId">Identifier of the entity the event is about.</param>
    /// <param name="payload">Body, serialised to JSON.</param>
    /// <typeparam name="TPayload">Payload type.</typeparam>
    OutboxMessage Publish<TPayload>(Guid tenantId, string eventType, string aggregateId, TPayload payload);

    /// <summary>Queues an event whose body is already JSON.</summary>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="eventType">Event name.</param>
    /// <param name="aggregateId">Identifier of the entity the event is about.</param>
    /// <param name="payloadJson">Body, already serialised.</param>
    OutboxMessage PublishJson(Guid tenantId, string eventType, string aggregateId, string payloadJson);
}

/// <summary>Writes outbox rows through the caller's <see cref="HookRelayDbContext"/>.</summary>
/// <param name="dbContext">The context the caller's business write is already using.</param>
/// <param name="timeProvider">Clock.</param>
public sealed class OutboxEventPublisher(HookRelayDbContext dbContext, TimeProvider timeProvider)
    : IWebhookEventPublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HookRelayDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public OutboxMessage Publish<TPayload>(
        Guid tenantId,
        string eventType,
        string aggregateId,
        TPayload payload) =>
        PublishJson(tenantId, eventType, aggregateId, JsonSerializer.Serialize(payload, SerializerOptions));

    /// <inheritdoc />
    public OutboxMessage PublishJson(Guid tenantId, string eventType, string aggregateId, string payloadJson)
    {
        OutboxMessage message = OutboxMessage.Enqueue(
            tenantId,
            eventType,
            aggregateId,
            payloadJson,
            _timeProvider.GetUtcNow());

        _dbContext.OutboxMessages.Add(message);
        return message;
    }
}
