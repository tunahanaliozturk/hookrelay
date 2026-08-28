using HookRelay.Domain.Deliveries;
using HookRelay.Domain.Endpoints;
using Shouldly;

namespace HookRelay.UnitTests;

/// <summary>Subscription matching, pausing, and the rotation overlap window.</summary>
public sealed class WebhookEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);

    [Theory]
    [InlineData("invoice.paid", "invoice.paid", true)]
    [InlineData("INVOICE.PAID", "invoice.paid", true)]
    [InlineData("invoice.*", "invoice.paid", true)]
    [InlineData("invoice.*", "invoice.payment_failed", true)]
    [InlineData("invoice.*", "subscription.updated", false)]
    [InlineData("*", "anything.at.all", true)]
    [InlineData("invoice.paid", "invoice.paid.late", false)]
    public void Subscriptions_match_exact_names_and_prefixes(string pattern, string eventType, bool expected)
    {
        Endpoint([pattern]).IsSubscribedTo(eventType).ShouldBe(expected);
    }

    [Fact]
    public void An_endpoint_must_subscribe_to_something()
    {
        Should.Throw<ArgumentException>(() => Endpoint([]));
        Should.Throw<ArgumentException>(() => Endpoint(["   "]));
    }

    [Fact]
    public void Rotation_keeps_the_previous_secret_reachable_for_deliveries_already_in_flight()
    {
        WebhookEndpoint endpoint = Endpoint(["*"]);
        endpoint.SecretVersion.ShouldBe(1);

        endpoint.RotateSecret("protected-v2", Now.AddMinutes(5));

        endpoint.SecretVersion.ShouldBe(2);
        endpoint.SecretRotatedAtUtc.ShouldBe(Now.AddMinutes(5));

        endpoint.TryGetProtectedSecret(2, out string current).ShouldBeTrue();
        current.ShouldBe("protected-v2");

        // A retry that was first dispatched before the rotation still verifies against what the receiver
        // was told at the time.
        endpoint.TryGetProtectedSecret(1, out string previous).ShouldBeTrue();
        previous.ShouldBe("protected-v1");
    }

    [Fact]
    public void The_overlap_window_is_one_rotation_deep()
    {
        WebhookEndpoint endpoint = Endpoint(["*"]);
        endpoint.RotateSecret("protected-v2", Now);
        endpoint.RotateSecret("protected-v3", Now);

        // Two rotations while a delivery was in flight is a real, if unusual, sequence. It fails loudly
        // instead of signing with a secret the receiver was never given.
        endpoint.TryGetProtectedSecret(1, out _).ShouldBeFalse();
        endpoint.TryGetProtectedSecret(3, out _).ShouldBeTrue();
    }

    [Fact]
    public void Pausing_and_resuming_move_between_active_and_paused()
    {
        WebhookEndpoint endpoint = Endpoint(["*"]);

        endpoint.Pause(Now);
        endpoint.Status.ShouldBe(EndpointStatus.Paused);

        endpoint.Resume(Now.AddMinutes(1));
        endpoint.Status.ShouldBe(EndpointStatus.Active);
        endpoint.UpdatedAtUtc.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public void A_disabled_endpoint_refuses_further_changes()
    {
        WebhookEndpoint endpoint = Endpoint(["*"]);
        endpoint.Disable(Now);

        Should.Throw<InvalidOperationException>(() => endpoint.Pause(Now));
        Should.Throw<InvalidOperationException>(() => endpoint.RotateSecret("protected-v2", Now));
        Should.Throw<InvalidOperationException>(() => endpoint.Resubscribe(["*"], Now));
    }

    [Fact]
    public void Resubscribing_replaces_the_list_and_trims_entries()
    {
        WebhookEndpoint endpoint = Endpoint(["invoice.*"]);

        endpoint.Resubscribe([" subscription.updated ", "", "invoice.paid"], Now);

        endpoint.SubscribedEventTypes.ShouldBe(["subscription.updated", "invoice.paid"]);
        endpoint.IsSubscribedTo("invoice.payment_failed").ShouldBeFalse();
    }

    private static WebhookEndpoint Endpoint(string[] eventTypes) => WebhookEndpoint.Register(
        Guid.NewGuid(),
        new Uri("https://hooks.example.com/events"),
        "Billing events",
        eventTypes,
        "protected-v1",
        OrderingStrategy.PerEndpoint,
        Now);
}
