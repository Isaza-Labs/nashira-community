using System.Net;
using System.Net.Sockets;

namespace nashira_backend.Services.Net;

// Blocks requests to localhost, private ranges, link-local and the cloud
// metadata endpoint. RFC-1918 ranges and carrier-grade NAT (100.64.0.0/10) pass
// when allowPrivate=true (on-prem integrations); IPv6 local ranges (unique-local,
// link-local, site-local), loopback, IPv4-compatible addresses and the 6to4
// (2002::/16) and Teredo (2001::/32) tunnelling forms never do. IPv4-mapped
// (::ffff:a.b.c.d) and NAT64 well-known-prefix (64:ff9b::a.b.c.d) addresses are
// judged by the IPv4 address they embed.
// Dev bypass: Security:AllowInternalUrls.
// Lifted from flow-weaver.
public sealed class UrlGuard : IUrlGuard
{
    private readonly bool _allowInternal;
    private readonly ILogger<UrlGuard> _logger;
    private readonly Func<string, IPAddress[]> _resolve;

    public UrlGuard(IConfiguration configuration, ILogger<UrlGuard> logger)
        : this(configuration, logger, Dns.GetHostAddresses)
    {
    }

    internal UrlGuard(IConfiguration configuration, ILogger<UrlGuard> logger, Func<string, IPAddress[]> resolve)
    {
        _allowInternal = configuration.GetValue<bool>("Security:AllowInternalUrls");
        _logger = logger;
        _resolve = resolve;
    }

    public void EnsureSafe(string url, bool allowPrivate = false) => Validate(url, allowPrivate, log: true);

    // The address a remote server asked to be redirected to is data the remote server chose: its host,
    // path and query can carry anything. It gets the same checks, in the same order, but is judged
    // without logging and without an error that repeats it, so the only record of it is the caller's
    // fixed refusal.
    public bool IsAllowedQuietly(string url, bool allowPrivate = false)
    {
        try
        {
            Validate(url, allowPrivate, log: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Validate(string url, bool allowPrivate, bool log)
    {
        if (_allowInternal) return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"Invalid URL: {url}");

        var host = uri.IdnHost;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host is "127.0.0.1" or "0.0.0.0" or "::1" or "169.254.169.254")
        {
            if (log) _logger.LogWarning("net.url_guard.blocked host={Host} reason=blocked_hostname", host);
            throw new InvalidOperationException($"SSRF blocked: requests to {host} are not allowed");
        }

        IPAddress[] addresses;
        try { addresses = IPAddress.TryParse(host, out var literal) ? [literal] : _resolve(host); }
        catch (SocketException ex)
        {
            if (log) _logger.LogWarning("net.url_guard.blocked host={Host} reason=dns_resolution_failed", host);
            throw new InvalidOperationException($"SSRF blocked: cannot resolve host {host}", ex);
        }

        if (addresses.Length == 0)
            throw new InvalidOperationException($"SSRF blocked: cannot resolve host {host}");

        // Check every DNS answer as well as literals. Normalize IPv4-mapped IPv6
        // before applying IPv4 rules so mapping cannot bypass metadata/private checks.
        // NAT64 addresses are normalized the same way: on an IPv6-only network with
        // DNS64 every IPv4-only public host resolves into 64:ff9b::/96, so the prefix
        // itself cannot be refused, but the IPv4 address it carries is what the NAT64
        // gateway will connect to and gets exactly the IPv4 rules.
        foreach (var address in addresses)
        {
            var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4()
                : IsNat64WellKnownPrefix(address) ? new IPAddress(address.GetAddressBytes().AsSpan(12, 4))
                : address;
            if (IPAddress.IsLoopback(ip))
                throw new InvalidOperationException($"SSRF blocked: {host} resolves to loopback {ip}");

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                var isPrivate =
                    b[0] == 10
                    || (b[0] == 172 && b[1] is >= 16 and <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    // 100.64.0.0/10, carrier-grade NAT (RFC 6598): shared address space
                    // inside a provider's network, not a public destination.
                    || (b[0] == 100 && b[1] is >= 64 and <= 127);

                if (isPrivate && !allowPrivate)
                {
                    if (log) _logger.LogWarning("net.url_guard.blocked host={Host} reason=private_address", host);
                    throw new InvalidOperationException($"SSRF blocked: {host} resolves to private address {ip}");
                }

                // Never bypassed by allowPrivate.
                if (b[0] == 169 && b[1] == 254)
                    throw new InvalidOperationException($"SSRF blocked: {host} resolves to link-local address {ip}");
                if (b[0] == 0)
                    throw new InvalidOperationException($"SSRF blocked: {host} resolves to reserved address {ip}");
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var bytes = ip.GetAddressBytes();
                if ((bytes[0] & 0xfe) == 0xfc)
                    throw new InvalidOperationException($"SSRF blocked: {host} resolves to IPv6 unique-local address {ip}");
                if (ip.IsIPv6LinkLocal)
                    throw new InvalidOperationException($"SSRF blocked: {host} resolves to IPv6 link-local address {ip}");
                // fec0::/10, deprecated but still routed on some networks. Never bypassed by allowPrivate.
                if (ip.IsIPv6SiteLocal)
                    throw new InvalidOperationException($"SSRF blocked: {host} resolves to IPv6 site-local address {ip}");
                // IPv4-compatible (::a.b.c.d, the whole ::/96 block) is deprecated and reserved; it can spell any
                // IPv4 target, so it is refused rather than mapped. Covers :: and ::1 as well.
                if (bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
                    throw new InvalidOperationException($"SSRF blocked: {host} resolves to IPv4-compatible IPv6 address {ip}");
                if (ip.Equals(IPAddress.IPv6Any))
                    throw new InvalidOperationException($"SSRF blocked: {host} resolves to IPv6 unspecified address {ip}");
                // 6to4 (2002::/16) and Teredo (2001::/32) are deprecated tunnelling forms; both carry an
                // IPv4 address that a relay connects to, so like IPv4-compatible they can spell any IPv4
                // target. Refused rather than mapped, whatever allowPrivate says.
                if (bytes[0] == 0x20 && bytes[1] == 0x02)
                    throw new InvalidOperationException($"SSRF blocked: {host} resolves to 6to4 IPv6 address {ip}");
                if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0)
                    throw new InvalidOperationException($"SSRF blocked: {host} resolves to Teredo IPv6 address {ip}");
            }
        }

        if (log) _logger.LogDebug("net.url_guard.ok host={Host}", host);
    }

    // 64:ff9b::/96 (RFC 6052), the NAT64 well-known prefix: the last four bytes are an IPv4 address.
    private static bool IsNat64WellKnownPrefix(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b
            && bytes.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0;
    }
}
