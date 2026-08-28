using HookRelay.ChaosReceiver;
using HookRelay.Domain.Deliveries;
using HookRelay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace HookRelay.IntegrationTests;

/// <summary>
/// The retry ladder, the dead-letter store, and the replay that closes the loop.
/// </summary>
[Collection(InfrastructureFixtureBinding.Name)]
public sealed class BackoffAndDeadLetterTests(ContainerFixture containers)
{
    /// <summary>
    /// A threshold high enough that the circuit never opens. These tests are about the ladder and the
    /// dead-letter store, and an open circuit would replace the HTTP outcomes they assert on with
    /// CircuitOpen. Breaker behaviour has its own suite.
    /// </summary>
    private static readonly Dictionary<string, string?> NoCircuitBreaker = new(StringComparer.Ordinal)
    {
        ["HookRelay:Delivery:CircuitMinimumThroughput"] = "1000",
    };

    private static readonly TimeSpan[] Ladder =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
    ];

    [Fact]
    public async Task Retries_land_on_the_configured_schedule()
    {
        // The claim in the docs is a specific ladder, so the test reads the recorded attempt timestamps
        // and checks them against it. "Eventually retries a few times" is not the same promise.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(
            containers,
            "backoff_schedule",
            settings: NoCircuitBreaker);
        (var endpoint, _) = await harness.RegisterEndpointAsync("a", failureRate: 1);

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);

        foreach (TimeSpan delay in Ladder)
        {
            await harness.StepAsync();
            harness.Clock!.Advance(delay);
        }

        await harness.StepAsync();

        Delivery delivery = (await harness.DeliveriesAsync(endpoint.Id)).Single();
        List<DeliveryAttempt> attempts = await harness.AttemptsAsync(delivery.Id);

        attempts.Count.ShouldBe(Ladder.Length + 1);

        for (int i = 0; i < Ladder.Length; i++)
        {
            TimeSpan actual = attempts[i + 1].AttemptedAtUtc - attempts[i].AttemptedAtUtc;
            TimeSpan expected = Ladder[i];

            actual.ShouldBeGreaterThanOrEqualTo(expected * 0.9, $"gap before attempt {i + 2}");
            actual.ShouldBeLessThanOrEqualTo(expected * 1.1, $"gap before attempt {i + 2}");
        }
    }

    [Fact]
    public async Task Every_attempt_is_recorded_including_the_ones_that_failed()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(
            containers,
            "backoff_log",
            settings: NoCircuitBreaker);
        (var endpoint, _) = await harness.RegisterEndpointAsync("a", failureRate: 1);

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);
        await harness.DrainAsync(TimeSpan.FromSeconds(1));

        Delivery delivery = (await harness.DeliveriesAsync(endpoint.Id)).Single();
        List<DeliveryAttempt> attempts = await harness.AttemptsAsync(delivery.Id);

        attempts.Count.ShouldBe(Ladder.Length + 1);
        attempts.ShouldAllBe(attempt => attempt.Outcome == AttemptOutcome.HttpError);
        attempts.ShouldAllBe(attempt => attempt.StatusCode == 500);
        attempts.Select(static attempt => attempt.AttemptNumber).ShouldBe([1, 2, 3, 4]);

        // The last attempt has nothing scheduled after it, which is what the delivery log shows a support
        // engineer as "this one is finished, and it did not succeed".
        attempts[^1].NextAttemptAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task A_delivery_that_runs_out_of_attempts_lands_in_the_dead_letter_store_with_its_payload()
    {
        await using PipelineHarness harness = await PipelineHarness.StartAsync(
            containers,
            "backoff_dlq",
            settings: NoCircuitBreaker);
        (var endpoint, _) = await harness.RegisterEndpointAsync("a", failureRate: 1);

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);
        await harness.DrainAsync(TimeSpan.FromSeconds(1));

        Delivery delivery = (await harness.DeliveriesAsync(endpoint.Id)).Single();
        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.AttemptCount.ShouldBe(Ladder.Length + 1);

        DeadLetter deadLetter = (await harness.DeadLettersAsync(endpoint.Id)).Single();
        deadLetter.DeliveryId.ShouldBe(delivery.Id);
        deadLetter.AttemptCount.ShouldBe(Ladder.Length + 1);
        deadLetter.PayloadJson.ShouldContain("\"sequence\": 1");
        deadLetter.ReplayedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task Replaying_after_the_endpoint_is_fixed_completes_the_delivery()
    {
        // The full cycle a support engineer walks: watch it fail, see it dead-lettered, get the customer to
        // fix their endpoint, replay, and confirm from the log that it went through.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(
            containers,
            "backoff_replay",
            settings: NoCircuitBreaker);
        (var endpoint, string secret) = await harness.RegisterEndpointAsync("a", failureRate: 1);

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);
        await harness.DrainAsync(TimeSpan.FromSeconds(1));

        Delivery deadLettered = (await harness.DeliveriesAsync(endpoint.Id)).Single();
        deadLettered.Status.ShouldBe(DeliveryStatus.DeadLettered);

        harness.Chaos.Configure("a", new SlotBehaviour(secret));

        await using (AsyncServiceScope scope = harness.CreateScope())
        {
            HookRelayDbContext dbContext = scope.ServiceProvider.GetRequiredService<HookRelayDbContext>();
            Delivery delivery = await dbContext.Deliveries.SingleAsync();
            DeadLetter deadLetter = await dbContext.DeadLetters.SingleAsync();

            DateTimeOffset now = harness.Clock!.GetUtcNow();
            delivery.Replay(now);
            deadLetter.MarkReplayed(now);
            await dbContext.SaveChangesAsync();
        }

        await harness.DrainAsync(TimeSpan.FromSeconds(1));

        Delivery replayed = (await harness.DeliveriesAsync(endpoint.Id)).Single();
        replayed.Status.ShouldBe(DeliveryStatus.Delivered);
        replayed.ReplayCount.ShouldBe(1);

        harness.Chaos.Accepted("a").Count.ShouldBe(1);

        // The dead-letter row survives the replay, so the history of what went wrong is not erased by
        // fixing it.
        DeadLetter kept = (await harness.DeadLettersAsync(endpoint.Id)).Single();
        kept.ReplayedAtUtc.ShouldNotBeNull();
        kept.ReplayCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_timeout_is_recorded_as_a_timeout_rather_than_a_generic_failure()
    {
        // A destination that accepts the connection and then goes quiet is a different problem from one
        // that answers 500, and the delivery log has to be able to tell a customer which one happened.
        await using PipelineHarness harness = await PipelineHarness.StartAsync(
            containers,
            "backoff_timeout",
            settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HookRelay:Delivery:RequestTimeout"] = "00:00:00.300",
            });

        (var endpoint, string secret) = await harness.RegisterEndpointAsync("a");
        harness.Chaos.Configure("a", new SlotBehaviour(secret, LatencyMs: 3000));

        await harness.PublishAsync("invoice.paid", "inv_1", sequence: 1);
        await harness.StepAsync();

        Delivery delivery = (await harness.DeliveriesAsync(endpoint.Id)).Single();
        List<DeliveryAttempt> attempts = await harness.AttemptsAsync(delivery.Id);

        attempts.Single().Outcome.ShouldBe(AttemptOutcome.Timeout);
        attempts.Single().StatusCode.ShouldBeNull();
        delivery.Status.ShouldBe(DeliveryStatus.Pending);
    }
}
