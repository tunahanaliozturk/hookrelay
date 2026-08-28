using System.Globalization;

namespace HookRelay.Domain.Deliveries;

/// <summary>
/// Builds the string that scopes the ordering guarantee. It is used three times over: as the Kafka
/// partition key, as the head-of-line claim key in the dispatcher, and as the grouping key the
/// ordering tests assert against.
/// </summary>
public static class OrderingKey
{
    /// <summary>Builds the ordering key for a delivery.</summary>
    /// <param name="endpointId">Destination endpoint.</param>
    /// <param name="aggregateId">Identifier of the entity the event is about, for example an invoice id.</param>
    /// <param name="strategy">How wide the ordered stream should be.</param>
    public static string For(Guid endpointId, string aggregateId, OrderingStrategy strategy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateId);

        return strategy switch
        {
            OrderingStrategy.PerEndpoint => string.Create(
                CultureInfo.InvariantCulture,
                $"ep:{endpointId:N}"),
            OrderingStrategy.PerEndpointAndAggregate => string.Create(
                CultureInfo.InvariantCulture,
                $"ep:{endpointId:N}|ag:{aggregateId}"),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown ordering strategy."),
        };
    }
}
