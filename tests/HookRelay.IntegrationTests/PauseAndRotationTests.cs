using System.Text.Json;
using HookRelay.ChaosReceiver;
using HookRelay.Domain.Deliveries;
using HookRelay.Domain.Endpoints;
using HookRelay.Domain.Security;
using HookRelay.Domain.Signing;
using HookRelay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace HookRelay.IntegrationTests;

/// <summary>
/// Two endpoint lifecycle promises: a pause loses nothing, and a rotation does not break a retry that was
/// already in flight.
/// </summary>
[Collection(InfrastructureFixtureBinding.Name)]
public sealed class PauseAndRotationTests(ContainerFixture containers)
{
    [Fact]
    public async Task A_paused_endpoint_queues_events_and_resumes_in_order()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "pause_resume");
        (var endpoint, _) = await harness.RegisterEndpointAsync("a");

        await SetStatusAsync(harness, endpoint.Id, EndpointStatus.Paused);

        for (int i = 1; i <= 5; i++)
        {
            await harness.PublishAsync("invoice.paid", $"inv_{i}", i);
        }

        await harness.DrainAsync(TimeSpan.FromMilliseconds(300), maxRounds: 6);

        // Fan-out still happened. The deliveries exist and are waiting, which is the difference between
        // pausing and dropping.
        List<Delivery> queued = await harness.DeliveriesAsync(endpoint.Id);
        queued.Count.ShouldBe(5);
        queued.ShouldAllBe(delivery => delivery.Status == DeliveryStatus.Pending);
        queued.ShouldAllBe(delivery => delivery.AttemptCount == 0);
        harness.Chaos.Received("a").ShouldBeEmpty();

        await SetStatusAsync(harness, endpoint.Id, EndpointStatus.Active);
        await harness.DrainAsync();

        int[] sequences =
        [
            .. harness.Chaos.Accepted("a").Select(static request =>
                JsonDocument.Parse(request.Body).RootElement.GetProperty("sequence").GetInt32())
        ];

        sequences.ShouldBe([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task Pausing_does_not_burn_the_retry_ladder()
    {
        // If a paused endpoint kept retrying to exhaustion, unpausing would find everything already in the
        // dead-letter store. Pausing has to stop the clock, not run it faster.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "pause_ladder");
        (var endpoint, _) = await harness.RegisterEndpointAsync("a");

        await SetStatusAsync(harness, endpoint.Id, EndpointStatus.Paused);
        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);

        await harness.DrainAsync(TimeSpan.FromSeconds(5), maxRounds: 6);

        (await harness.DeadLettersAsync(endpoint.Id)).ShouldBeEmpty();
        (await harness.DeliveriesAsync(endpoint.Id)).Single().AttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_retry_already_in_flight_keeps_signing_with_the_secret_it_started_with()
    {
        // Rotating a secret must not retroactively invalidate a delivery a customer is already verifying.
        // The delivery pins its secret version at fan-out, so the receiver holding the old secret still
        // accepts the retry.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "rotate_inflight");
        (var endpoint, string originalSecret) = await harness.RegisterEndpointAsync("a", failureRate: 1);

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);
        await harness.StepAsync();

        harness.Chaos.Received("a").Count.ShouldBe(1);
        harness.Chaos.Received("a")[0].Signature.ShouldBe(SignatureVerificationResult.Valid);

        // The customer rotates. Their receiver is still verifying with the secret it already has.
        string rotatedSecret = await RotateAsync(harness, endpoint.Id);
        rotatedSecret.ShouldNotBe(originalSecret);
        harness.Chaos.Configure("a", new SlotBehaviour(originalSecret));

        harness.Clock!.Advance(TimeSpan.FromMilliseconds(250));
        await harness.StepAsync();

        harness.Chaos.Received("a").Count.ShouldBe(2);
        harness.Chaos.Received("a")[1].Signature.ShouldBe(SignatureVerificationResult.Valid);
    }

    [Fact]
    public async Task An_event_published_after_a_rotation_uses_the_new_secret()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "rotate_new");
        (var endpoint, _) = await harness.RegisterEndpointAsync("a");

        string rotatedSecret = await RotateAsync(harness, endpoint.Id);
        harness.Chaos.Configure("a", new SlotBehaviour(rotatedSecret));

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);
        await harness.DrainAsync();

        harness.Chaos.Accepted("a").Count.ShouldBe(1);
        harness.Chaos.Received("a")[0].Signature.ShouldBe(SignatureVerificationResult.Valid);
    }

    [Fact]
    public async Task A_delivery_pinned_to_a_secret_older_than_the_overlap_window_fails_loudly()
    {
        // Two rotations while a delivery is in flight leaves no secret the receiver was ever told about.
        // Signing with the current one anyway would produce a signature the customer cannot verify and no
        // explanation in the log, so the delivery is dead-lettered with the reason instead.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(containers, "rotate_gap");
        (var endpoint, _) = await harness.RegisterEndpointAsync("a", failureRate: 1);

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);
        await harness.StepAsync();

        await RotateAsync(harness, endpoint.Id);
        await RotateAsync(harness, endpoint.Id);

        harness.Clock!.Advance(TimeSpan.FromMilliseconds(250));
        await harness.StepAsync();

        Delivery delivery = (await harness.DeliveriesAsync(endpoint.Id)).Single();
        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.LastError.ShouldNotBeNull().ShouldContain("rotated out of the overlap window");

        (await harness.DeadLettersAsync(endpoint.Id)).Count.ShouldBe(1);
    }

    private static async Task SetStatusAsync(PipelineHarness harness, Guid endpointId, EndpointStatus status)
    {
        await using AsyncServiceScope scope = harness.CreateScope();
        HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();
        TimeProvider time = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        WebhookEndpoint endpoint = await dbContext.Endpoints.SingleAsync(e => e.Id == endpointId);

        if (status is EndpointStatus.Paused)
        {
            endpoint.Pause(time.GetUtcNow());
        }
        else
        {
            endpoint.Resume(time.GetUtcNow());
        }

        await dbContext.SaveChangesAsync();
    }

    private static async Task<string> RotateAsync(PipelineHarness harness, Guid endpointId)
    {
        await using AsyncServiceScope scope = harness.CreateScope();
        HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();
        ISecretProtector protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        TimeProvider time = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        WebhookEndpoint endpoint = await dbContext.Endpoints.SingleAsync(e => e.Id == endpointId);
        string secret = WebhookSecret.Generate();

        endpoint.RotateSecret(protector.Protect(secret), time.GetUtcNow());
        await dbContext.SaveChangesAsync();

        return secret;
    }
}
