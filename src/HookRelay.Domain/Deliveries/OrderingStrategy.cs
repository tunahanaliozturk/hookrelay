namespace HookRelay.Domain.Deliveries;

/// <summary>
/// Scope of the in-order delivery guarantee. The guarantee only ever holds inside one ordering key,
/// never globally, and the key is what decides how much concurrency an endpoint gets.
/// </summary>
public enum OrderingStrategy
{
    /// <summary>
    /// One ordered stream per endpoint. Strongest guarantee, lowest concurrency: a delivery stuck in
    /// retry holds back every later event for that endpoint.
    /// </summary>
    PerEndpoint = 0,

    /// <summary>
    /// One ordered stream per (endpoint, aggregate). Events for different aggregates flow in parallel,
    /// so a stuck invoice does not hold back an unrelated subscription.
    /// </summary>
    PerEndpointAndAggregate = 1,
}
