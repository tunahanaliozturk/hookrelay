using System.Net;
using System.Net.Sockets;

namespace HookRelay.Domain.Security;

/// <summary>Outcome of checking a customer-supplied destination URL.</summary>
public enum UrlValidationResult
{
    /// <summary>Safe to deliver to.</summary>
    Allowed = 0,

    /// <summary>Not a well-formed absolute URL.</summary>
    NotAbsolute = 1,

    /// <summary>Scheme is something other than https, or http where http is not permitted.</summary>
    SchemeNotAllowed = 2,

    /// <summary>The URL embeds credentials, which would end up in logs and proxy traces.</summary>
    CredentialsInUrl = 3,

    /// <summary>The host resolves to an address inside the delivery fleet's own network.</summary>
    PrivateNetwork = 4,
}

/// <summary>
/// Decides whether the delivery fleet is willing to send to a URL.
/// </summary>
/// <remarks>
/// A webhook sender is a request forwarder that anyone with an account can point wherever they like, which
/// makes server-side request forgery the defining vulnerability of this kind of service rather than an edge
/// case. Registering <c>http://169.254.169.254/latest/meta-data/</c> and reading the delivery log turns the
/// fleet into a cloud metadata proxy. Blocking private ranges here covers literal addresses; the send path
/// re-checks after DNS resolution, because a hostname can point anywhere and can change between the two.
/// </remarks>
public static class WebhookUrlPolicy
{
    /// <summary>Checks a destination URL.</summary>
    /// <param name="url">The customer-supplied URL.</param>
    /// <param name="allowInsecureHttp">Permit plain http. Only ever enabled for local development and tests.</param>
    /// <param name="allowPrivateNetworks">Permit loopback and private ranges. Only ever enabled for local development and tests.</param>
    public static UrlValidationResult Validate(string? url, bool allowInsecureHttp, bool allowPrivateNetworks)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            return UrlValidationResult.NotAbsolute;
        }

        return Validate(parsed, allowInsecureHttp, allowPrivateNetworks);
    }

    /// <summary>Checks a destination URL.</summary>
    /// <param name="url">The customer-supplied URL.</param>
    /// <param name="allowInsecureHttp">Permit plain http. Only ever enabled for local development and tests.</param>
    /// <param name="allowPrivateNetworks">Permit loopback and private ranges. Only ever enabled for local development and tests.</param>
    public static UrlValidationResult Validate(Uri url, bool allowInsecureHttp, bool allowPrivateNetworks)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri)
        {
            return UrlValidationResult.NotAbsolute;
        }

        bool schemeAllowed = url.Scheme == Uri.UriSchemeHttps
            || (allowInsecureHttp && url.Scheme == Uri.UriSchemeHttp);
        if (!schemeAllowed)
        {
            return UrlValidationResult.SchemeNotAllowed;
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            return UrlValidationResult.CredentialsInUrl;
        }

        if (!allowPrivateNetworks
            && IPAddress.TryParse(url.Host.Trim('[', ']'), out IPAddress? literal)
            && IsPrivate(literal))
        {
            return UrlValidationResult.PrivateNetwork;
        }

        return UrlValidationResult.Allowed;
    }

    /// <summary>
    /// True when an address belongs to a range that should never be reachable from the delivery fleet.
    /// Called again after DNS resolution, since a public hostname can resolve to a private address.
    /// </summary>
    /// <param name="address">The resolved address.</param>
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> octets = stackalloc byte[4];
            if (!address.TryWriteBytes(octets, out _))
            {
                return true;
            }

            return octets[0] switch
            {
                10 => true,                                     // 10.0.0.0/8
                127 => true,                                    // loopback
                169 when octets[1] == 254 => true,              // link-local, includes cloud metadata
                172 when octets[1] >= 16 && octets[1] <= 31 => true,  // 172.16.0.0/12
                192 when octets[1] == 168 => true,              // 192.168.0.0/16
                100 when octets[1] >= 64 && octets[1] <= 127 => true, // carrier-grade NAT
                0 => true,                                      // "this network"
                _ => false,
            };
        }

        return address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6UniqueLocal
            || address.IsIPv6Multicast;
    }
}
