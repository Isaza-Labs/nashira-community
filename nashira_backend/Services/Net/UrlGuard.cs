using System.Net;
using System.Net.Sockets;

namespace nashira_backend.Services.Net;

// Blocks requests to localhost, private ranges, link-local and the cloud
// metadata endpoint. RFC-1918 ranges pass when allowPrivate=true (on-prem
// integrations); IPv6 local ranges (unique-local, link-local, site-local),
// loopback and IPv4-compatible addresses never do.
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

    public void EnsureSafe(string url, bool allowPrivate = false)
    {
        if (_allowInternal) return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"Invalid URL: {url}");

        var host = uri.IdnHost;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host is "127.0.0.1" or "0.0.0.0" or "::1" or "169.254.169.254")
        {
            _logger.LogWarning("net.url_guard.blocked host={Host} reason=blocked_hostname", host);
            throw new InvalidOperationException($"SSRF blocked: requests to {host} are not allowed");
        }

        IPAddress[] addresses;
        try { addresses = IPAddress.TryParse(host, out var literal) ? [literal] : _resolve(host); }
        catch (SocketException ex)
        {
            _logger.LogWarning("net.url_guard.blocked host={Host} reason=dns_resolution_failed", host);
            throw new InvalidOperationException($"SSRF blocked: cannot resolve host {host}", ex);
        }

        if (addresses.Length == 0)
            throw new InvalidOperationException($"SSRF blocked: cannot resolve host {host}");

        // Check every DNS answer as well as literals. Normalize IPv4-mapped IPv6
        // before applying IPv4 rules so mapping cannot bypass metadata/private checks.
        foreach (var address in addresses)
        {
            var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (IPAddress.IsLoopback(ip))
                throw new InvalidOperationException($"SSRF blocked: {host} resolves to loopback {ip}");

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                var isPrivate =
                    b[0] == 10
                    || (b[0] == 172 && b[1] is >= 16 and <= 31)
                    || (b[0] == 192 && b[1] == 168);

                if (isPrivate && !allowPrivate)
                {
                    _logger.LogWarning("net.url_guard.blocked host={Host} reason=private_address", host);
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
            }
        }

        _logger.LogDebug("net.url_guard.ok host={Host}", host);
    }
}
