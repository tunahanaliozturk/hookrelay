using System.Text.Json;
using HookRelay.ChaosReceiver;
using HookRelay.Domain.Deliveries;
using HookRelay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace HookRelay.IntegrationTests;

/// <summary>
/// The headline claim, run against the real stack.
/// </summary>
/// <remarks>
/// Everything here goes through the actual wiring: background pollers on their own schedule, signals
/// travelling through Kafka, a system clock, and a receiver that fails roughly a third of the time for real.
/// The stepped tests prove the logic; this one proves the assembly.
/// </remarks>
[Collection(InfrastructureFixtureBinding.Name)]
public sealed class EndToEndChaosTests(ContainerFixture containers)
{
    private const double FailureRate = 0.3;
    private const int EventCount = 60;

    /// <summary>
    /// Enough rungs that exhaustion is not a coin flip. With a 30% independent failure rate per attempt,
    /// thirteen attempts put the odds of a delivery running out of road at roughly one in six million.
    /// </summary>
    private static Dictionary<string, string?> CompressedLadder()
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int i = 0; i < 12; i++)
        {
            settings[$"HookRelay:Delivery:RetryDelays:{i}"] = "00:00:00.150";
        }

        settings["HookRelay:Delivery:JitterRatio"] = "0.1";
        settings["HookRelay:Delivery:CircuitMinimumThroughput"] = "20";
        return settings;
    }

    [Fact]
    public async Task Every_event_reaches_a_receiver_that_fails_a_third_of_the_time()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(
            containers,
            "chaos_live",
            HarnessMode.Live,
            CompressedLadder());

        (var first, _) = await harness.RegisterEndpointAsync("one", failureRate: FailureRate);
        (var second, _) = await harness.RegisterEndpointAsync("two", failureRate: FailureRate);

        for (int i = 1; i <= EventCount; i++)
        {
            await harness.PublishAsync("invoice.paid", $"inv_{i}", i);
        }

        await PipelineHarness.WaitForAsync(
            async () => !await harness.HasPendingWorkAsync(),
            TimeSpan.FromMinutes(2),
            "every delivery to reach a terminal state");

        foreach (Guid endpointId in new[] { first.Id, second.Id })
        {
            List<Delivery> deliveries = await harness.DeliveriesAsync(endpointId);
            deliveries.Count.ShouldBe(EventCount);
            deliveries.ShouldAllBe(delivery => delivery.Status == DeliveryStatus.Delivered);
        }

        foreach (string slot in new[] { "one", "two" })
        {
            int[] accepted =
            [
                .. harness.Chaos.Accepted(slot).Select(static request =>
                    JsonDocument.Parse(request.Body).RootElement.GetProperty("sequence").GetInt32())
            ];

            // Every published event arrived at least once. At-least-once permits more, so the assertion is
            // on the set, not the count.
            accepted.Distinct().Order().ShouldBe(Enumerable.Range(1, EventCount));

            // And it arrived in order, which is the guarantee that survives the retries.
            accepted.ShouldBe(Enumerable.Range(1, EventCount).ToArray());

            // A receiver that failed a third of the time saw meaningfully more traffic than it accepted.
            // Without this the test would still pass if the chaos never actually fired.
            harness.Chaos.Received(slot).Count.ShouldBeGreaterThan(accepted.Length);
        }
    }

    [Fact]
    public async Task The_delivery_log_explains_what_happened_to_every_event()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(
            containers,
            "chaos_log",
            HarnessMode.Live,
            CompressedLadder());

        (var endpoint, _) = await harness.RegisterEndpointAsync("one", failureRate: FailureRate);

        for (int i = 1; i <= 20; i++)
        {
            await harness.PublishAsync("invoice.paid", $"inv_{i}", i);
        }

        await PipelineHarness.WaitForAsync(
            async () => !await harness.HasPendingWorkAsync(),
            TimeSpan.FromMinutes(2),
            "every delivery to reach a terminal state");

        await using AsyncServiceScope scope = harness.CreateScope();
        HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();

        int attempts = await dbContext.DeliveryAttempts.CountAsync();
        int deliveries = await dbContext.Deliveries.CountAsync();

        // Every attempt was written as it happened, not reconstructed afterwards, so with a third of them
        // failing there are more attempt rows than deliveries.
        attempts.ShouldBeGreaterThan(deliveries);

        foreach (Delivery delivery in await harness.DeliveriesAsync(endpoint.Id))
        {
            List<DeliveryAttempt> history = await harness.AttemptsAsync(delivery.Id);

            history.Count.ShouldBe(delivery.AttemptCount);
            history.Select(static attempt => attempt.AttemptNumber)
                .ShouldBe(Enumerable.Range(1, history.Count));
            history[^1].Outcome.ShouldBe(AttemptOutcome.Success);
        }
    }

    [Fact]
    public async Task Signatures_survive_the_whole_pipeline()
    {
        // The receiver runs the reference verifier the docs point customers at, so a change that breaks
        // signing shows up here rather than in a support ticket.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(
            containers,
            "chaos_signing",
            HarnessMode.Live,
            CompressedLadder());

        await harness.RegisterEndpointAsync("one", verifySignature: true);

        for (int i = 1; i <= 10; i++)
        {
            await harness.PublishAsync("invoice.paid", $"inv_{i}", i);
        }

        await PipelineHarness.WaitForAsync(
            async () => !await harness.HasPendingWorkAsync(),
            TimeSpan.FromMinutes(1),
            "every delivery to reach a terminal state");

        IReadOnlyList<ReceivedRequest> received = harness.Chaos.Received("one");
        received.Count.ShouldBe(10);
        received.ShouldAllBe(request => request.Signature == Domain.Signing.SignatureVerificationResult.Valid);
        received.ShouldAllBe(request => request.DeliveryId != null && request.DeliveryId != string.Empty);
        received.ShouldAllBe(request => request.EventType == "invoice.paid");
        received.ShouldAllBe(request => request.Attempt == 1);
    }
}
