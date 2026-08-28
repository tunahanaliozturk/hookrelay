using HookRelay.Domain.Deliveries;
using HookRelay.Domain.Outbox;
using HookRelay.Infrastructure.Persistence;
using HookRelay.Infrastructure.Relay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace HookRelay.IntegrationTests;

/// <summary>
/// The durability claim: an event that was written is never lost, and never fanned out twice.
/// </summary>
[Collection(InfrastructureFixtureBinding.Name)]
public sealed class OutboxDurabilityTests(ContainerFixture containers)
{
    [Fact]
    public async Task An_event_reaches_every_subscribed_endpoint_exactly_once()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "outbox_fanout");
        await harness.RegisterEndpointAsync("a");
        await harness.RegisterEndpointAsync("b");

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);
        await harness.StepAsync();

        await using AsyncServiceScope scope = harness.CreateScope();
        HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();

        (await dbContext.Deliveries.CountAsync()).ShouldBe(2);
        (await dbContext.OutboxMessages.CountAsync(message => message.Status == OutboxStatus.Dispatched))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Only_subscribed_endpoints_receive_an_event()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "outbox_subs");
        (var billing, _) = await harness.RegisterEndpointAsync("billing", ["invoice.*"]);
        (var accounts, _) = await harness.RegisterEndpointAsync("accounts", ["customer.created"]);

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);
        await harness.StepAsync();

        (await harness.DeliveriesAsync(billing.Id)).Count.ShouldBe(1);
        (await harness.DeliveriesAsync(accounts.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_event_with_no_subscribers_is_recorded_as_fanned_out_to_nobody()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "outbox_nosubs");
        await harness.RegisterEndpointAsync("billing", ["invoice.*"]);

        await harness.PublishAsync("customer.created", "cus_1", sequence: 1);
        await harness.StepAsync();

        await using AsyncServiceScope scope = harness.CreateScope();
        OutboxMessage message = await scope.ServiceProvider
            .GetRequiredService<HookRelayDbContext>()
            .OutboxMessages
            .SingleAsync();

        message.Status.ShouldBe(OutboxStatus.Dispatched);
        message.FanOutCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_fan_out_pass_that_never_commits_leaves_the_outbox_untouched()
    {
        // Fan-out claims rows and marks them dispatched inside one transaction. A pass that dies before
        // committing has to leave the rows exactly as they were, so the next instance picks them up and
        // every event is still fanned out once. This is the reason the claim is not a read-then-update pair.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "outbox_rollback");
        (var endpoint, _) = await harness.RegisterEndpointAsync("a");

        const int EventCount = 25;
        for (int i = 1; i <= EventCount; i++)
        {
            await harness.PublishAsync("invoice.paid", $"inv_{i}", i);
        }

        var relay = harness.Services.GetRequiredService<OutboxRelayService>();
        using var abandoned = new CancellationTokenSource();
        await abandoned.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => relay.RunOnceAsync(abandoned.Token));

        await using (AsyncServiceScope scope = harness.CreateScope())
        {
            HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();

            // Nothing half-applied: no orphan deliveries, no rows quietly marked dispatched.
            (await dbContext.Deliveries.CountAsync()).ShouldBe(0);
            (await dbContext.OutboxMessages.CountAsync(message => message.Status == OutboxStatus.Pending))
                .ShouldBe(EventCount);
        }

        await harness.DrainAsync();

        List<Delivery> deliveries = await harness.DeliveriesAsync(endpoint.Id);
        deliveries.Count.ShouldBe(EventCount);
        deliveries.Select(static delivery => delivery.OutboxMessageId).Distinct().Count().ShouldBe(EventCount);
        deliveries.ShouldAllBe(delivery => delivery.Status == DeliveryStatus.Delivered);
    }

    [Fact]
    public async Task Concurrent_relay_instances_never_fan_the_same_row_out_twice()
    {
        // Two relays racing on the same table is the normal production shape, not an edge case. SKIP LOCKED
        // is what keeps them off each other's rows, and a duplicate here would mean every customer getting
        // every event twice.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "outbox_race");
        (var endpoint, _) = await harness.RegisterEndpointAsync("a");

        const int EventCount = 40;
        for (int i = 1; i <= EventCount; i++)
        {
            await harness.PublishAsync("invoice.paid", $"inv_{i}", i);
        }

        var relay = harness.Services.GetRequiredService<OutboxRelayService>();

        for (int round = 0; round < EventCount; round++)
        {
            await Task.WhenAll(
                relay.RunOnceAsync(CancellationToken.None),
                relay.RunOnceAsync(CancellationToken.None),
                relay.RunOnceAsync(CancellationToken.None));
        }

        List<Delivery> deliveries = await harness.DeliveriesAsync(endpoint.Id);
        deliveries.Count.ShouldBe(EventCount);
        deliveries.Select(static delivery => delivery.OutboxMessageId).Distinct().Count().ShouldBe(EventCount);
    }
}
