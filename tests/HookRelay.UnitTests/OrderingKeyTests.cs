using HookRelay.Domain.Deliveries;
using Shouldly;

namespace HookRelay.UnitTests;

/// <summary>The key that scopes the ordering guarantee, and therefore how much concurrency an endpoint gets.</summary>
public sealed class OrderingKeyTests
{
    private static readonly Guid EndpointA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EndpointB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Per_endpoint_puts_every_event_for_one_endpoint_in_the_same_stream()
    {
        string first = OrderingKey.For(EndpointA, "inv_1", OrderingStrategy.PerEndpoint);
        string second = OrderingKey.For(EndpointA, "inv_2", OrderingStrategy.PerEndpoint);

        first.ShouldBe(second);
    }

    [Fact]
    public void Per_endpoint_and_aggregate_gives_each_entity_its_own_stream()
    {
        string first = OrderingKey.For(EndpointA, "inv_1", OrderingStrategy.PerEndpointAndAggregate);
        string second = OrderingKey.For(EndpointA, "inv_2", OrderingStrategy.PerEndpointAndAggregate);

        first.ShouldNotBe(second);
        first.ShouldContain("inv_1");
    }

    [Fact]
    public void Two_endpoints_never_share_a_stream()
    {
        OrderingKey.For(EndpointA, "inv_1", OrderingStrategy.PerEndpoint)
            .ShouldNotBe(OrderingKey.For(EndpointB, "inv_1", OrderingStrategy.PerEndpoint));
    }

    [Fact]
    public void An_aggregate_id_is_required()
    {
        Should.Throw<ArgumentException>(() =>
            OrderingKey.For(EndpointA, "  ", OrderingStrategy.PerEndpoint));
    }
}
