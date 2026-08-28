using HookRelay.Domain.Deliveries;
using HookRelay.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace HookRelay.UnitTests;

/// <summary>
/// That the retry ladder is actually what configuration says it is.
/// </summary>
/// <remarks>
/// This exists because getting it wrong is silent. An earlier version defaulted the ladder to a pre-filled
/// list, and the configuration binder appended to it instead of replacing it, so the test suite believed it
/// had compressed the schedule to milliseconds while every retry was still waiting out a 30 second rung.
/// Nothing failed. The tests just quietly stopped covering retries.
/// </remarks>
public sealed class DeliveryOptionsBindingTests
{
    [Fact]
    public void An_unconfigured_ladder_is_the_published_one()
    {
        RetrySchedule schedule = Bind(new Dictionary<string, string?>(StringComparer.Ordinal)).ToRetrySchedule();

        schedule.MaxAttempts.ShouldBe(7);
        schedule.BaseDelayAfter(1).ShouldBe(TimeSpan.FromSeconds(30));
        schedule.BaseDelayAfter(6).ShouldBe(TimeSpan.FromHours(24));
    }

    [Fact]
    public void A_configured_ladder_replaces_the_default_rather_than_extending_it()
    {
        RetrySchedule schedule = Bind(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HookRelay:Delivery:RetryDelays:0"] = "00:00:00.200",
            ["HookRelay:Delivery:RetryDelays:1"] = "00:00:00.400",
            ["HookRelay:Delivery:RetryDelays:2"] = "00:00:00.800",
        }).ToRetrySchedule();

        schedule.MaxAttempts.ShouldBe(4);
        schedule.BaseDelayAfter(1).ShouldBe(TimeSpan.FromMilliseconds(200));
        schedule.BaseDelayAfter(3).ShouldBe(TimeSpan.FromMilliseconds(800));
        schedule.RetryWindow.ShouldBe(TimeSpan.FromMilliseconds(1400));
    }

    [Fact]
    public void The_other_delivery_settings_bind_too()
    {
        DeliveryOptions options = Bind(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HookRelay:Delivery:JitterRatio"] = "0",
            ["HookRelay:Delivery:RequestTimeout"] = "00:00:02",
            ["HookRelay:Delivery:CircuitMinimumThroughput"] = "3",
            ["HookRelay:Delivery:AllowInsecureHttp"] = "true",
        });

        options.JitterRatio.ShouldBe(0);
        options.RequestTimeout.ShouldBe(TimeSpan.FromSeconds(2));
        options.CircuitMinimumThroughput.ShouldBe(3);
        options.AllowInsecureHttp.ShouldBeTrue();
        options.AllowPrivateNetworkDestinations.ShouldBeFalse();
    }

    private static DeliveryOptions Bind(IReadOnlyDictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var options = new DeliveryOptions();
        configuration.GetSection(DeliveryOptions.SectionName).Bind(options);
        return options;
    }
}
