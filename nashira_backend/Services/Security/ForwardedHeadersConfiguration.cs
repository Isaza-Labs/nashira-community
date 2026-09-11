using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace nashira_backend.Services.Security;

// Restores the real client address — and the browser-facing scheme and host —
// when the backend sits behind a proxy.
//
// It always does in the shipped topology: the browser talks to the SvelteKit
// container, which proxies /api to the backend over the compose network. Every
// request therefore arrives from the frontend container's address, which breaks
// both the audit trail and the per-IP login limiter (see ClientIp).
//
// Trust is opt-in and explicit. X-Forwarded-For is attacker-controlled input:
// honouring it from an arbitrary caller would let anyone forge its own
// address, defeating the login limiter far more thoroughly than the bug this
// fixes and poisoning the audit trail with fabricated origins. So the
// forwarded address is only accepted from proxies the operator declared:
//
//   Network__TrustedProxies=172.27.0.0/16          (a CIDR — a whole network)
//   Network__TrustedProxies=10.1.2.3,10.1.2.4      (individual proxy hosts)
//   Network__TrustedProxies=172.27.0.0/16,10.1.2.3 (both, comma-separated)
//
// Unset => nothing is trusted and RemoteIpAddress stays the immediate peer.
// That is the pre-existing (wrong, but not forgeable) behaviour, which is the
// right default for an operator who has not thought about their topology yet.
public static class ForwardedHeadersConfiguration
{
    public const string ConfigKey = "Network:TrustedProxies";

    public static IServiceCollection AddForwardedHeadersFromConfig(
        this IServiceCollection services, IConfiguration configuration)
    {
        var raw = configuration[ConfigKey];

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            // The defaults trust loopback only, which is never what a
            // containerised deployment wants. Clear them so the configured
            // list is the whole truth rather than an addition to a default
            // the operator can't see.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            var trusted = 0;
            foreach (var entry in Split(raw))
            {
                if (TryParseNetwork(entry, out var network))
                {
                    options.KnownIPNetworks.Add(network);
                    trusted++;
                }
                else if (IPAddress.TryParse(entry, out var proxy))
                {
                    options.KnownProxies.Add(proxy);
                    trusted++;
                }
                // Unparseable entries are dropped rather than throwing:
                // refusing to boot over a typo in an observability setting
                // would be a worse failure than logging the wrong IP. The
                // startup log reports what was actually accepted.
            }

            // CRITICAL, and the opposite of what it looks like: an EMPTY
            // KnownIPNetworks + KnownProxies does NOT mean "trust nobody" to
            // ForwardedHeadersMiddleware — it means "skip the check", i.e.
            // trust EVERYONE. Leaving the feature enabled with both lists
            // cleared would let any caller forge X-Forwarded-For, dodge the
            // per-IP login limiter and write fabricated origins into the
            // audit log: strictly worse than the bug this class exists to
            // fix. So with nothing configured we disable the middleware
            // outright and keep the immediate peer.
            //
            // Host is included for the same reason as the address: behind the
            // shipped proxy Request.Host is the internal name (backend:8080),
            // and anything built from it — the MCP OAuth redirect_uri above
            // all — is a URL no browser can reach and providers reject.
            // The frontend proxy forwards the browser-facing host; honour it
            // from the same trusted proxies only.
            options.ForwardedHeaders = trusted == 0
                ? ForwardedHeaders.None
                : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
                  | ForwardedHeaders.XForwardedHost;

            // One hop by default (browser → frontend → backend). Deployments
            // that add another proxy in front — cloudflared, nginx, an ALB —
            // need this raised to match, or the address resolves to the
            // innermost proxy instead of the client.
            options.ForwardLimit = configuration.GetValue<int?>("Network:ForwardLimit") ?? 1;
        });

        return services;
    }

    // Describes the effective configuration for the boot log, so an operator
    // can tell "trusting nothing" from "trusting the compose network" without
    // reading the container's environment.
    public static string Describe(IConfiguration configuration)
    {
        var entries = Split(configuration[ConfigKey]).ToList();
        return entries.Count == 0
            ? "none (X-Forwarded-For ignored; client IPs will be the immediate peer)"
            : string.Join(",", entries);
    }

    private static IEnumerable<string> Split(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParseNetwork(string entry, out System.Net.IPNetwork network)
    {
        network = default;
        var slash = entry.IndexOf('/');
        if (slash < 0) return false;

        var prefix = entry[..slash];
        var lengthText = entry[(slash + 1)..];
        if (!IPAddress.TryParse(prefix, out var address)) return false;
        if (!int.TryParse(lengthText, out var prefixLength)) return false;

        var maxLength = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefixLength < 0 || prefixLength > maxLength) return false;

        // System.Net.IPNetwork insists on the canonical network address (host
        // bits zero), while an operator writing "172.27.0.1/16" clearly means
        // the /16 it sits in — mask the host bits rather than dropping the
        // whole entry over them.
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsLeft = prefixLength - i * 8;
            if (bitsLeft >= 8) continue;
            bytes[i] = bitsLeft <= 0 ? (byte)0 : (byte)(bytes[i] & (0xFF << (8 - bitsLeft)));
        }

        network = new System.Net.IPNetwork(new IPAddress(bytes), prefixLength);
        return true;
    }
}
