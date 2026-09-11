using System.Net;
using System.Net.Sockets;

namespace nashira_backend.Services.Net;

// Blocks requests to localhost, private ranges, link-local and the cloud
// metadata endpoint. Private ranges pass when allowPrivate=true (on-prem
// integrations); loopback + 169.254 never do. Dev bypass: Security:AllowInternalUrls.
// Lifted from flow-weaver.
public sealed class UrlGuard : IUrlGuard
{
    private readonly bool _allowInternal;
    private readonly ILogger<UrlGuard> _logger;

    public UrlGuard(IConfiguration configuration, ILogger<UrlGuard> logger)
    {
        _allowInternal = configuration.GetValue<bool>("Security:AllowInternalUrls");
        _logger = logger;
    }

    public void EnsureSafe(string url, bool allowPrivate = false)
    {
        if (_allowInternal) return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Invalid URL: {url}");

        var host = uri.Host;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host is "127.0.0.1" or "0.0.0.0" or "::1" or "169.254.169.254")
        {
            _logger.LogWarning("net.url_guard.blocked host={Host} reason=blocked_hostname", host);
            throw new InvalidOperationException($"SSRF blocked: requests to {host} are not allowed");
        }

        IPAddress[] addresses;
        try { addresses = Dns.GetHostAddresses(host); }
        catch (SocketException ex)
        {
            _logger.LogWarning("net.url_guard.blocked host={Host} reason=dns_resolution_failed", host);
            throw new InvalidOperationException($"SSRF blocked: cannot resolve host {host}", ex);
        }

        foreach (var ip in addresses)
        {
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
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.Equals(IPAddress.IPv6Loopback))
            {
                throw new InvalidOperationException($"SSRF blocked: {host} resolves to IPv6 loopback {ip}");
            }
        }

        _logger.LogDebug("net.url_guard.ok host={Host}", host);
    }
}
