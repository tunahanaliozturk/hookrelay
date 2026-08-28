using System.Text.Json;
using HookRelay.ChaosReceiver;
using HookRelay.Domain.Deliveries;
using Shouldly;

namespace HookRelay.IntegrationTests;

/// <summary>
/// The ordering claim, scoped to one ordering key and never claimed globally.
/// </summary>
/// <remarks>
/// A single-key happy-path test proves nothing here. What breaks ordering in practice is concurrency
/// across many keys, a retry that overtakes the event behind it, and a slow destination that lets later
/// work pass it. Each of those gets its own test.
/// </remarks>
[Collection(InfrastructureFixtureBinding.Name)]
public sealed class OrderingTests(ContainerFixture containers)
{
    [Fact]
    public async Task Events_for_one_endpoint_arrive_in_the_order_they_were_produced()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "order_single");
        await harness.RegisterEndpointAsync("a");

        for (int i = 1; i <= 20; i++)
        {
            await harness.PublishAsync("invoice.paid", $"inv_{i}", i);
        }

        await harness.DrainAsync();

        SequencesReceivedOn(harness, "a").ShouldBe(Enumerable.Range(1, 20).ToArray());
    }

    [Fact]
    public async Task A_delivery_stuck_in_retry_holds_its_own_stream_and_nothing_overtakes_it()
    {
        // The failure mode this exists to stop: event 1 fails, event 2 succeeds immediately, and the
        // customer's state machine sees "cancelled" before it saw "created".
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "order_retry");
        await harness.RegisterEndpointAsync("a");

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);
        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 2);

        // First event fails twice before the receiver recovers.
        harness.Chaos.Configure("a", Behaviour(harness, failureRate: 1));
        await harness.StepAsync();

        SequencesReceivedOn(harness, "a", acceptedOnly: false).ShouldBe([1]);

        harness.Clock!.Advance(TimeSpan.FromMilliseconds(250));
        await harness.StepAsync();

        // Still only event 1 has ever been offered. Event 2 has not been dispatched at all.
        SequencesReceivedOn(harness, "a", acceptedOnly: false).ShouldBe([1, 1]);

        harness.Chaos.Configure("a", Behaviour(harness, failureRate: 0));
        harness.Clock.Advance(TimeSpan.FromMilliseconds(450));
        await harness.DrainAsync();

        SequencesReceivedOn(harness, "a").ShouldBe([1, 2]);
    }

    [Fact]
    public async Task One_slow_endpoint_does_not_delay_another()
    {
        // Ordering is per key. Two endpoints are two keys, so a stream that is crawling must not become
        // everyone else's problem.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "order_isolated");
        await harness.RegisterEndpointAsync("slow", latencyMs: 150);
        await harness.RegisterEndpointAsync("fast");

        for (int i = 1; i <= 6; i++)
        {
            await harness.PublishAsync("invoice.paid", $"inv_{i}", i);
        }

        await harness.DrainAsync();

        SequencesReceivedOn(harness, "slow").ShouldBe([1, 2, 3, 4, 5, 6]);
        SequencesReceivedOn(harness, "fast").ShouldBe([1, 2, 3, 4, 5, 6]);
    }

    [Fact]
    public async Task Interleaved_publishing_across_many_keys_keeps_every_key_in_order()
    {
        // Per-aggregate ordering: unrelated invoices flow in parallel, and each invoice's own stream stays
        // in sequence. This is the assertion that a partition-key-only design fails once retries are involved.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "order_multikey");
        await harness.RegisterEndpointAsync("a", strategy: OrderingStrategy.PerEndpointAndAggregate);

        string[] aggregates = ["inv_a", "inv_b", "inv_c", "inv_d", "inv_e"];
        const int PerAggregate = 8;

        for (int sequence = 1; sequence <= PerAggregate; sequence++)
        {
            foreach (string aggregate in aggregates)
            {
                await harness.PublishAsync("invoice.paid", aggregate, sequence);
            }
        }

        await harness.DrainAsync();

        IReadOnlyList<ReceivedRequest> received = harness.Chaos.Accepted("a");
        received.Count.ShouldBe(aggregates.Length * PerAggregate);

        foreach (string aggregate in aggregates)
        {
            int[] sequences =
            [
                .. received
                    .Select(static request => JsonDocument.Parse(request.Body).RootElement)
                    .Where(payload => payload.GetProperty("aggregateId").GetString() == aggregate)
                    .Select(static payload => payload.GetProperty("sequence").GetInt32())
            ];

            sequences.ShouldBe(Enumerable.Range(1, PerAggregate).ToArray(), $"aggregate {aggregate}");
        }
    }

    private static SlotBehaviour Behaviour(PipelineHarness harness, double failureRate) =>
        new(harness.Chaos.State.BehaviourFor("a").Secret, failureRate);

    private static int[] SequencesReceivedOn(PipelineHarness harness, string slot, bool acceptedOnly = true)
    {
        IReadOnlyList<ReceivedRequest> requests = acceptedOnly
            ? harness.Chaos.Accepted(slot)
            : harness.Chaos.Received(slot);

        return
        [
            .. requests.Select(static request =>
                JsonDocument.Parse(request.Body).RootElement.GetProperty("sequence").GetInt32())
        ];
    }
}
