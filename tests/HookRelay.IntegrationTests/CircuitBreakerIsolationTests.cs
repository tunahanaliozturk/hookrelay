using HookRelay.Domain.Deliveries;
using Shouldly;

namespace HookRelay.IntegrationTests;

/// <summary>
/// One customer's dead endpoint must not become every other customer's problem.
/// </summary>
/// <remarks>
/// A shared resilience pipeline is the easy mistake here, and it is invisible until the day one destination
/// goes down and delivery stops for everybody. These tests assert the isolation directly rather than
/// trusting that keying the registry was enough.
/// </remarks>
[Collection(InfrastructureFixtureBinding.Name)]
public sealed class CircuitBreakerIsolationTests(ContainerFixture containers)
{
    [Fact]
    public async Task An_endpoint_that_keeps_failing_has_its_circuit_opened()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "circuit_open");
        (var endpoint, _) = await harness.RegisterEndpointAsync("broken", failureRate: 1);

        // Three failures inside the sampling window is the configured threshold.
        for (int i = 1; i <= 4; i++)
        {
            await harness.PublishAsync("invoice.paid", $"inv_{i}", i);
        }

        await harness.DrainAsync(TimeSpan.FromMilliseconds(250));

        List<AttemptOutcome> outcomes = [];
        foreach (Delivery delivery in await harness.DeliveriesAsync(endpoint.Id))
        {
            outcomes.AddRange((await harness.AttemptsAsync(delivery.Id))
                .Select(static attempt => attempt.Outcome));
        }

        outcomes.ShouldContain(AttemptOutcome.HttpError);
        outcomes.ShouldContain(AttemptOutcome.CircuitOpen);
    }

    [Fact]
    public async Task An_open_circuit_stops_the_requests_rather_than_just_the_successes()
    {
        // The reason for a breaker is to take load off a destination that is already struggling. If the
        // requests still go out, all the breaker has done is relabel the failures.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "circuit_quiet");
        (var endpoint, _) = await harness.RegisterEndpointAsync("broken", failureRate: 1);

        for (int i = 1; i <= 6; i++)
        {
            await harness.PublishAsync("invoice.paid", $"inv_{i}", i);
        }

        await harness.DrainAsync(TimeSpan.FromMilliseconds(250));

        int attemptsRecorded = 0;
        foreach (Delivery delivery in await harness.DeliveriesAsync(endpoint.Id))
        {
            attemptsRecorded += (await harness.AttemptsAsync(delivery.Id)).Count;
        }

        int requestsThatReachedTheReceiver = harness.Chaos.Received("broken").Count;

        requestsThatReachedTheReceiver.ShouldBeLessThan(attemptsRecorded);
    }

    [Fact]
    public async Task A_broken_endpoint_does_not_affect_a_healthy_one()
    {
        // The headline isolation claim. Endpoint A is dead, endpoint B is fine, and B should not be able
        // to tell that A exists.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "circuit_isolation");
        (var broken, _) = await harness.RegisterEndpointAsync("broken", failureRate: 1);
        (var healthy, _) = await harness.RegisterEndpointAsync("healthy");

        for (int i = 1; i <= 8; i++)
        {
            await harness.PublishAsync("invoice.paid", $"inv_{i}", i);
        }

        await harness.DrainAsync(TimeSpan.FromMilliseconds(250));

        List<Delivery> healthyDeliveries = await harness.DeliveriesAsync(healthy.Id);
        healthyDeliveries.Count.ShouldBe(8);
        healthyDeliveries.ShouldAllBe(delivery => delivery.Status == DeliveryStatus.Delivered);

        // Every one of them went through first time. A shared breaker would have short-circuited these.
        healthyDeliveries.ShouldAllBe(delivery => delivery.AttemptCount == 1);
        harness.Chaos.Accepted("healthy").Count.ShouldBe(8);

        (await harness.DeliveriesAsync(broken.Id))
            .ShouldAllBe(delivery => delivery.Status == DeliveryStatus.DeadLettered);
    }

    [Fact]
    public async Task A_recovered_endpoint_starts_receiving_again_after_the_circuit_half_opens()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(
            containers,
            "circuit_recovery",
            settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                // A long ladder, so the delivery survives long enough for the breaker to reopen.
                ["HookRelay:Delivery:RetryDelays:0"] = "00:00:00.100",
                ["HookRelay:Delivery:RetryDelays:1"] = "00:00:00.100",
                ["HookRelay:Delivery:RetryDelays:2"] = "00:00:00.100",
                ["HookRelay:Delivery:RetryDelays:3"] = "00:00:00.100",
                ["HookRelay:Delivery:RetryDelays:4"] = "00:00:00.100",
                ["HookRelay:Delivery:RetryDelays:5"] = "00:00:00.100",
                ["HookRelay:Delivery:RetryDelays:6"] = "00:00:00.100",
                ["HookRelay:Delivery:RetryDelays:7"] = "00:00:00.100",
                ["HookRelay:Delivery:CircuitBreakDuration"] = "00:00:00.500",
            });

        (var endpoint, string secret) = await harness.RegisterEndpointAsync("flaky", failureRate: 1);

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);

        for (int i = 0; i < 4; i++)
        {
            await harness.StepAsync();
            harness.Clock!.Advance(TimeSpan.FromMilliseconds(120));
        }

        harness.Chaos.Configure("flaky", new ChaosReceiver.SlotBehaviour(secret));

        // The breaker samples on wall-clock time, which the harness clock does not control, so the recovery
        // window is waited out rather than skipped.
        await Task.Delay(TimeSpan.FromMilliseconds(700));

        await harness.DrainAsync(TimeSpan.FromMilliseconds(120));

        Delivery delivery = (await harness.DeliveriesAsync(endpoint.Id)).Single();
        delivery.Status.ShouldBe(DeliveryStatus.Delivered);
    }
}
