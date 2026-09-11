using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Services.Net;

namespace nashira_backend.Tests;

// SSRF guard behavior (security-critical) using IP literals — no DNS/network.
public class UrlGuardTests
{
    private static UrlGuard Guard() => new(new ConfigurationBuilder().Build(), NullLogger<UrlGuard>.Instance);

    [Fact]
    public void Blocks_metadata_loopback_and_private_but_allows_private_when_opted_in()
    {
        var g = Guard();

        Assert.Throws<InvalidOperationException>(() => g.EnsureSafe("http://169.254.169.254/latest/meta-data"));
        Assert.Throws<InvalidOperationException>(() => g.EnsureSafe("http://127.0.0.1:8080"));
        Assert.Throws<InvalidOperationException>(() => g.EnsureSafe("http://10.0.0.5", allowPrivate: false));

        // On-prem integrations in RFC-1918 ranges: allowed only when opted in.
        g.EnsureSafe("http://10.0.0.5", allowPrivate: true);
        g.EnsureSafe("http://192.168.1.20", allowPrivate: true);

        // Link-local/metadata is never bypassed, even with allowPrivate.
        Assert.Throws<InvalidOperationException>(() => g.EnsureSafe("http://169.254.169.254", allowPrivate: true));
    }
}
