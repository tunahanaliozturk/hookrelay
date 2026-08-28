using HookRelay.Domain.Deliveries;
using HookRelay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace HookRelay.IntegrationTests;

/// <summary>
/// What happens to a delivery whose worker never came back.
/// </summary>
/// <remarks>
/// Without this sweep, a worker that dies between claiming a delivery and recording the attempt strands it
/// in flight forever. The event is not lost from the database, which is the part that matters, but it is
/// never delivered either, and nothing in the system notices.
/// </remarks>
[Collection(InfrastructureFixtureBinding.Name)]
public sealed class StaleClaimTests(ContainerFixture containers)
{
    [Fact]
    public async Task A_claim_older_than_the_timeout_is_returned_to_the_pending_pool()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "stale_reclaim");
        (var endpoint, _) = await harness.RegisterEndpointAsync("a");

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);
        await ClaimWithoutProcessingAsync(harness);

        (await harness.DeliveriesAsync(endpoint.Id)).Single().Status.ShouldBe(DeliveryStatus.InFlight);

        // Not yet stale.
        (await harness.SweepStaleClaimsAsync()).ShouldBe(0);

        harness.Clock!.Advance(TimeSpan.FromSeconds(45));
        (await harness.SweepStaleClaimsAsync()).ShouldBe(1);

        Delivery reclaimed = (await harness.DeliveriesAsync(endpoint.Id)).Single();
        reclaimed.Status.ShouldBe(DeliveryStatus.Pending);
        reclaimed.ClaimedAtUtc.ShouldBeNull();

        // The attempt was never made, so nothing was burned off the ladder.
        reclaimed.AttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_reclaimed_delivery_goes_on_to_be_delivered()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "stale_recovery");
        (var endpoint, _) = await harness.RegisterEndpointAsync("a");

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);
        await ClaimWithoutProcessingAsync(harness);

        harness.Clock!.Advance(TimeSpan.FromSeconds(45));
        await harness.SweepStaleClaimsAsync();
        await harness.DrainAsync();

        (await harness.DeliveriesAsync(endpoint.Id)).Single().Status.ShouldBe(DeliveryStatus.Delivered);
        harness.Chaos.Accepted("a").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_stranded_claim_blocks_its_own_ordering_key_until_it_is_reclaimed()
    {
        // Head-of-line blocking is the intended behaviour, not a bug: letting the next event past a claim
        // nobody is working on would deliver it out of order.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "stale_blocking");
        (var endpoint, _) = await harness.RegisterEndpointAsync("a");

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);
        await ClaimWithoutProcessingAsync(harness);

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 2);
        await harness.StepAsync();

        harness.Chaos.Received("a").ShouldBeEmpty();

        harness.Clock!.Advance(TimeSpan.FromSeconds(45));
        await harness.SweepStaleClaimsAsync();
        await harness.DrainAsync();

        (await harness.DeliveriesAsync(endpoint.Id))
            .ShouldAllBe(delivery => delivery.Status == DeliveryStatus.Delivered);
        harness.Chaos.Accepted("a").Count.ShouldBe(2);
    }

    /// <summary>Fans out and claims, then throws the signal away, which is what a dying worker looks like.</summary>
    private static async Task ClaimWithoutProcessingAsync(PipelineHarness harness)
    {
        var relay = harness.Services.GetRequiredService<Infrastructure.Relay.OutboxRelayService>();
        var dispatcher = harness.Services.GetRequiredService<Infrastructure.Relay.DeliveryDispatcherService>();

        await relay.RunOnceAsync(CancellationToken.None);
        await dispatcher.RunOnceAsync(CancellationToken.None);

        // The signal is dropped on the floor. From the pipeline's point of view the worker took the work
        // and never came back.
        harness.DiscardPendingSignals().ShouldBeGreaterThan(0);

        await using AsyncServiceScope scope = harness.CreateScope();
        HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();
        (await dbContext.Deliveries.CountAsync(delivery => delivery.Status == DeliveryStatus.InFlight))
            .ShouldBeGreaterThan(0);
    }
}
