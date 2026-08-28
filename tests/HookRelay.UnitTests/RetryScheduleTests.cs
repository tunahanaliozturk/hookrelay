using HookRelay.Domain.Deliveries;
using Shouldly;

namespace HookRelay.UnitTests;

/// <summary>Covers the published ladder and the jitter bounds the adherence check relies on.</summary>
public sealed class RetryScheduleTests
{
    [Fact]
    public void Default_matches_the_published_ladder()
    {
        RetrySchedule schedule = RetrySchedule.Default;

        schedule.MaxAttempts.ShouldBe(7);
        schedule.BaseDelayAfter(1).ShouldBe(TimeSpan.FromSeconds(30));
        schedule.BaseDelayAfter(2).ShouldBe(TimeSpan.FromMinutes(2));
        schedule.BaseDelayAfter(3).ShouldBe(TimeSpan.FromMinutes(10));
        schedule.BaseDelayAfter(4).ShouldBe(TimeSpan.FromHours(1));
        schedule.BaseDelayAfter(5).ShouldBe(TimeSpan.FromHours(6));
        schedule.BaseDelayAfter(6).ShouldBe(TimeSpan.FromHours(24));
    }

    [Fact]
    public void Retry_window_is_bounded_at_just_over_31_hours()
    {
        RetrySchedule.Default.RetryWindow.ShouldBe(
            TimeSpan.FromHours(31) + TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Exhausting_the_ladder_reports_no_further_attempt()
    {
        RetrySchedule schedule = RetrySchedule.Default;

        schedule.TryGetDelay(schedule.MaxAttempts - 1, 0.5, out _).ShouldBeTrue();
        schedule.TryGetDelay(schedule.MaxAttempts, 0.5, out TimeSpan delay).ShouldBeFalse();
        delay.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.5d)]
    [InlineData(0.999d)]
    public void Jitter_stays_inside_the_configured_band(double sample)
    {
        RetrySchedule schedule = RetrySchedule.Default;
        TimeSpan expected = schedule.BaseDelayAfter(1);

        schedule.TryGetDelay(1, sample, out TimeSpan delay).ShouldBeTrue();

        delay.ShouldBeGreaterThanOrEqualTo(expected * (1 - schedule.JitterRatio));
        delay.ShouldBeLessThanOrEqualTo(expected * (1 + schedule.JitterRatio));
    }

    [Fact]
    public void Jitter_moves_the_delay_in_both_directions()
    {
        RetrySchedule schedule = RetrySchedule.Default;

        schedule.TryGetDelay(1, 0d, out TimeSpan lowest);
        schedule.TryGetDelay(1, 0.5d, out TimeSpan middle);
        schedule.TryGetDelay(1, 0.999d, out TimeSpan highest);

        lowest.ShouldBeLessThan(middle);
        highest.ShouldBeGreaterThan(middle);
        middle.ShouldBe(schedule.BaseDelayAfter(1));
    }

    [Fact]
    public void A_compressed_ladder_keeps_the_same_shape()
    {
        // This is the shape CI runs, so the whole retry cycle finishes in seconds rather than a day and a half.
        var compressed = new RetrySchedule(
            [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800)],
            jitterRatio: 0);

        compressed.MaxAttempts.ShouldBe(4);
        compressed.RetryWindow.ShouldBe(TimeSpan.FromMilliseconds(1400));
        compressed.TryGetDelay(2, 0.7, out TimeSpan delay).ShouldBeTrue();
        delay.ShouldBe(TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public void Construction_rejects_nonsense()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RetrySchedule([]));
        Should.Throw<ArgumentOutOfRangeException>(() => new RetrySchedule([TimeSpan.Zero]));
        Should.Throw<ArgumentOutOfRangeException>(() => new RetrySchedule([TimeSpan.FromSeconds(-1)]));
        Should.Throw<ArgumentOutOfRangeException>(() => new RetrySchedule([TimeSpan.FromSeconds(1)], jitterRatio: 0.9));
    }
}
