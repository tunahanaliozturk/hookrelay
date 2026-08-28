using HookRelay.Domain.Deliveries;
using Shouldly;

namespace HookRelay.UnitTests;

/// <summary>Walks a delivery through the ladder, into the dead-letter store, and back out via replay.</summary>
public sealed class DeliveryLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);

    private static readonly RetrySchedule Schedule = new(
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)],
        jitterRatio: 0);

    [Fact]
    public void A_new_delivery_is_due_immediately()
    {
        Delivery delivery = NewDelivery();

        delivery.Status.ShouldBe(DeliveryStatus.Pending);
        delivery.AttemptCount.ShouldBe(0);
        delivery.NextAttemptAtUtc.ShouldBe(Now);
    }

    [Fact]
    public void Each_failure_books_the_next_slot_on_the_ladder()
    {
        Delivery delivery = NewDelivery();

        delivery.RecordFailure(Schedule, 0.5, "503", Now).ShouldBe(FailureOutcome.Retrying);
        delivery.NextAttemptAtUtc.ShouldBe(Now + TimeSpan.FromSeconds(30));

        delivery.RecordFailure(Schedule, 0.5, "503", Now.AddSeconds(30)).ShouldBe(FailureOutcome.Retrying);
        delivery.NextAttemptAtUtc.ShouldBe(Now.AddSeconds(30) + TimeSpan.FromMinutes(2));

        delivery.RecordFailure(Schedule, 0.5, "503", Now.AddMinutes(3)).ShouldBe(FailureOutcome.Retrying);
        delivery.AttemptCount.ShouldBe(3);
    }

    [Fact]
    public void Running_out_of_attempts_dead_letters_the_delivery()
    {
        Delivery delivery = NewDelivery();

        for (int i = 0; i < Schedule.MaxAttempts - 1; i++)
        {
            delivery.RecordFailure(Schedule, 0.5, "503", Now).ShouldBe(FailureOutcome.Retrying);
        }

        delivery.RecordFailure(Schedule, 0.5, "503 Service Unavailable", Now).ShouldBe(FailureOutcome.DeadLettered);

        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.AttemptCount.ShouldBe(Schedule.MaxAttempts);
        delivery.CompletedAtUtc.ShouldBe(Now);
        delivery.LastError.ShouldBe("503 Service Unavailable");
    }

    [Fact]
    public void A_dead_letter_carries_the_payload_so_replay_does_not_depend_on_the_outbox_row()
    {
        Delivery delivery = NewDelivery();
        while (delivery.Status is not DeliveryStatus.DeadLettered)
        {
            delivery.RecordFailure(Schedule, 0.5, "timeout", Now);
        }

        DeadLetter deadLetter = DeadLetter.From(delivery, "timeout", Now);

        deadLetter.DeliveryId.ShouldBe(delivery.Id);
        deadLetter.PayloadJson.ShouldBe("""{"amount":4200}""");
        deadLetter.AttemptCount.ShouldBe(Schedule.MaxAttempts);
        deadLetter.ReplayedAtUtc.ShouldBeNull();
    }

    [Fact]
    public void Replay_gives_the_delivery_a_fresh_ladder()
    {
        Delivery delivery = NewDelivery();
        while (delivery.Status is not DeliveryStatus.DeadLettered)
        {
            delivery.RecordFailure(Schedule, 0.5, "timeout", Now);
        }

        delivery.Replay(Now.AddHours(2));

        delivery.Status.ShouldBe(DeliveryStatus.Pending);
        delivery.AttemptCount.ShouldBe(0);
        delivery.NextAttemptAtUtc.ShouldBe(Now.AddHours(2));
        delivery.CompletedAtUtc.ShouldBeNull();
        delivery.ReplayCount.ShouldBe(1);
    }

    [Fact]
    public void Only_a_dead_lettered_delivery_can_be_replayed()
    {
        Delivery delivery = NewDelivery();

        Should.Throw<InvalidOperationException>(() => delivery.Replay(Now));
    }

    [Fact]
    public void A_claim_can_only_be_taken_once()
    {
        Delivery delivery = NewDelivery();

        delivery.MarkInFlight(Now);
        delivery.Status.ShouldBe(DeliveryStatus.InFlight);
        delivery.ClaimedAtUtc.ShouldBe(Now);

        Should.Throw<InvalidOperationException>(() => delivery.MarkInFlight(Now));
    }

    [Fact]
    public void Releasing_a_claim_makes_the_delivery_due_again()
    {
        Delivery delivery = NewDelivery();
        delivery.MarkInFlight(Now);

        delivery.ReleaseClaim(Now.AddSeconds(5));

        delivery.Status.ShouldBe(DeliveryStatus.Pending);
        delivery.ClaimedAtUtc.ShouldBeNull();
        delivery.NextAttemptAtUtc.ShouldBe(Now.AddSeconds(5));
    }

    [Fact]
    public void Success_clears_the_claim_and_the_last_error()
    {
        Delivery delivery = NewDelivery();
        delivery.RecordFailure(Schedule, 0.5, "503", Now);
        delivery.MarkInFlight(Now.AddSeconds(30));

        delivery.RecordSuccess(Now.AddSeconds(31));

        delivery.Status.ShouldBe(DeliveryStatus.Delivered);
        delivery.AttemptCount.ShouldBe(2);
        delivery.LastError.ShouldBeNull();
        delivery.ClaimedAtUtc.ShouldBeNull();
    }

    [Fact]
    public void A_permanent_failure_skips_the_rest_of_the_ladder()
    {
        Delivery delivery = NewDelivery();

        delivery.FailPermanently("secret rotated out of the overlap window", Now);

        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.AttemptCount.ShouldBe(1);
    }

    [Fact]
    public void An_attempt_record_captures_when_the_next_one_is_due()
    {
        Delivery delivery = NewDelivery();
        delivery.RecordFailure(Schedule, 0.5, "503", Now);

        DeliveryAttempt attempt = DeliveryAttempt.Record(
            delivery,
            AttemptOutcome.HttpError,
            statusCode: 503,
            TimeSpan.FromMilliseconds(120),
            responseSnippet: "upstream unavailable",
            error: "Endpoint responded 503.",
            Now);

        attempt.AttemptNumber.ShouldBe(1);
        attempt.LatencyMs.ShouldBe(120);
        attempt.NextAttemptAtUtc.ShouldBe(Now + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_terminal_attempt_records_no_next_attempt()
    {
        Delivery delivery = NewDelivery();
        delivery.RecordSuccess(Now);

        DeliveryAttempt attempt = DeliveryAttempt.Record(
            delivery,
            AttemptOutcome.Success,
            statusCode: 200,
            TimeSpan.FromMilliseconds(40),
            responseSnippet: "{\"received\":true}",
            error: null,
            Now);

        attempt.NextAttemptAtUtc.ShouldBeNull();
    }

    private static Delivery NewDelivery() => Delivery.Create(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "invoice.paid",
        """{"amount":4200}""",
        "ep:abc",
        secretVersion: 1,
        Now);
}
