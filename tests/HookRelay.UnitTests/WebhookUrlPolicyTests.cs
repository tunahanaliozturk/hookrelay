using System.Net;
using HookRelay.Domain.Security;
using Shouldly;

namespace HookRelay.UnitTests;

/// <summary>
/// Covers the destinations a webhook sender must refuse.
/// </summary>
/// <remarks>
/// A sender is a request forwarder anyone with an account can aim wherever they like, so this is the
/// service's most exposed surface. The cloud metadata address gets its own case because reaching it is the
/// difference between a bug and a credential leak.
/// </remarks>
public sealed class WebhookUrlPolicyTests
{
    [Fact]
    public void Https_to_a_public_host_is_allowed()
    {
        WebhookUrlPolicy.Validate("https://hooks.example.com/events", allowInsecureHttp: false, allowPrivateNetworks: false)
            .ShouldBe(UrlValidationResult.Allowed);
    }

    [Fact]
    public void Plain_http_is_refused_unless_it_is_explicitly_enabled()
    {
        WebhookUrlPolicy.Validate("http://hooks.example.com/events", allowInsecureHttp: false, allowPrivateNetworks: false)
            .ShouldBe(UrlValidationResult.SchemeNotAllowed);

        WebhookUrlPolicy.Validate("http://hooks.example.com/events", allowInsecureHttp: true, allowPrivateNetworks: false)
            .ShouldBe(UrlValidationResult.Allowed);
    }

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com")]
    public void Only_http_schemes_are_considered(string url)
    {
        WebhookUrlPolicy.Validate(url, allowInsecureHttp: true, allowPrivateNetworks: true)
            .ShouldBe(UrlValidationResult.SchemeNotAllowed);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void A_url_that_will_not_parse_is_refused(string? url)
    {
        WebhookUrlPolicy.Validate(url, allowInsecureHttp: true, allowPrivateNetworks: true)
            .ShouldBe(UrlValidationResult.NotAbsolute);
    }

    [Theory]
    [InlineData("/relative/path")]
    [InlineData("C:\\windows\\system32")]
    public void A_path_is_refused_whatever_the_platform_decides_to_call_it(string url)
    {
        // Uri parsing is platform dependent here. On Linux a leading slash parses as an absolute file
        // URI, on Windows it does not, so the two platforms disagree about which rule rejected it. Both
        // refuse it, which is the part that matters.
        WebhookUrlPolicy.Validate(url, allowInsecureHttp: true, allowPrivateNetworks: true)
            .ShouldNotBe(UrlValidationResult.Allowed);
    }

    [Fact]
    public void Credentials_in_the_url_are_refused()
    {
        // They would be written into the delivery log and every proxy trace along the way.
        WebhookUrlPolicy.Validate("https://user:pass@hooks.example.com/e", allowInsecureHttp: false, allowPrivateNetworks: false)
            .ShouldBe(UrlValidationResult.CredentialsInUrl);
    }

    [Theory]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://192.168.1.20/hook")]
    [InlineData("https://172.16.4.9/hook")]
    [InlineData("https://100.64.0.1/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[fd00::1]/hook")]
    public void Literal_addresses_inside_the_fleet_are_refused(string url)
    {
        WebhookUrlPolicy.Validate(url, allowInsecureHttp: false, allowPrivateNetworks: false)
            .ShouldBe(UrlValidationResult.PrivateNetwork);
    }

    [Fact]
    public void The_cloud_metadata_address_is_refused()
    {
        // 169.254.169.254 returns instance credentials on every major cloud. A sender that will POST to it
        // and show the response body in a delivery log is a credential-exfiltration endpoint.
        WebhookUrlPolicy.Validate(
            "https://169.254.169.254/latest/meta-data/iam/security-credentials/",
            allowInsecureHttp: false,
            allowPrivateNetworks: false)
            .ShouldBe(UrlValidationResult.PrivateNetwork);
    }

    [Fact]
    public void Private_addresses_are_allowed_when_a_local_run_opts_in()
    {
        WebhookUrlPolicy.Validate("http://127.0.0.1:5005/hooks/a", allowInsecureHttp: true, allowPrivateNetworks: true)
            .ShouldBe(UrlValidationResult.Allowed);
    }

    [Theory]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("2606:4700::1111", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.255.255.254", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    public void IsPrivate_classifies_resolved_addresses(string address, bool expected)
    {
        // This is the check the connect callback runs after DNS resolution, which is what closes the
        // rebinding gap a registration-time check on its own leaves open.
        WebhookUrlPolicy.IsPrivate(IPAddress.Parse(address)).ShouldBe(expected);
    }
}
