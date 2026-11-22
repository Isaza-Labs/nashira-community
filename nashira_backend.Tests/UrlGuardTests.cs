using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Services.Net;

namespace nashira_backend.Tests;

// SSRF guard behavior (security-critical) using IP literals — no DNS/network.
public class UrlGuardTests
{
    private static UrlGuard Guard() => new(new ConfigurationBuilder().Build(), NullLogger<UrlGuard>.Instance);

    [Theory]
    [InlineData("fc00::1")]
    [InlineData("fcff:ffff::1")]
    [InlineData("fd00::1")]
    [InlineData("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("::")]
    [InlineData("::ffff:169.254.169.254")]
    public void Blocks_ipv6_internal_and_mapped_metadata_addresses(string address)
    {
        foreach (var allowPrivate in new[] { false, true })
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Guard().EnsureSafe($"http://[{address}]:8080", allowPrivate));
            Assert.Contains("SSRF blocked", error.Message);
        }
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Existing_ipv6_loopback_protections_still_hold(string address)
        => Assert.Throws<InvalidOperationException>(() => Guard().EnsureSafe($"http://[{address}]"));

    [Fact]
    public void Public_ipv6_still_passes()
        => Guard().EnsureSafe("https://[2606:4700:4700::1111]");

    [Theory]
    [InlineData("fc00::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:169.254.169.254")]
    public void Every_resolved_address_is_checked_even_after_a_public_answer(string forbidden)
    {
        var calls = 0;
        var guard = new UrlGuard(new ConfigurationBuilder().Build(), NullLogger<UrlGuard>.Instance, host =>
        {
            Assert.Equal("test.invalid", host);
            calls++;
            return [IPAddress.Parse("2606:4700:4700::1111"), IPAddress.Parse(forbidden)];
        });

        var error = Assert.Throws<InvalidOperationException>(() => guard.EnsureSafe("https://test.invalid"));

        Assert.Equal(1, calls);
        Assert.Contains("SSRF blocked", error.Message);
    }

    [Fact]
    public void Dns_is_rechecked_on_each_validation()
    {
        var calls = 0;
        var guard = new UrlGuard(new ConfigurationBuilder().Build(), NullLogger<UrlGuard>.Instance,
            _ => [IPAddress.Parse(++calls == 1 ? "2606:4700:4700::1111" : "fd00::1")]);

        guard.EnsureSafe("https://test.invalid");
        Assert.Throws<InvalidOperationException>(() => guard.EnsureSafe("https://test.invalid/next"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Failed_or_empty_dns_answers_fail_closed()
    {
        var empty = new UrlGuard(new ConfigurationBuilder().Build(), NullLogger<UrlGuard>.Instance, _ => []);
        var failed = new UrlGuard(new ConfigurationBuilder().Build(), NullLogger<UrlGuard>.Instance,
            _ => throw new SocketException());

        Assert.Throws<InvalidOperationException>(() => empty.EnsureSafe("https://test.invalid"));
        Assert.Throws<InvalidOperationException>(() => failed.EnsureSafe("https://test.invalid"));
    }

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
