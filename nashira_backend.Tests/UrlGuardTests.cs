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

    // fec0::/10 is IPv6 site-local: the same class of address as unique-local, and just as
    // unreachable from a public integration. allowPrivate never opens it.
    [Theory]
    [InlineData("fec0::1")]
    [InlineData("FEC0::1")]
    [InlineData("fec0:0:0:0:0:0:0:1")]
    [InlineData("fec0:0:0:1::5")]
    [InlineData("fed0::1")]
    [InlineData("feff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    public void Blocks_ipv6_site_local_addresses_with_either_allow_private_value(string address)
    {
        foreach (var allowPrivate in new[] { false, true })
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Guard().EnsureSafe($"http://[{address}]:8080", allowPrivate));
            Assert.Contains("SSRF blocked", error.Message);
        }
    }

    // A site-local address in a DNS answer is refused too, even after a public one.
    [Theory]
    [InlineData("fec0::1")]
    [InlineData("fec0:0:0:1::5")]
    public void A_site_local_dns_answer_is_refused_with_either_allow_private_value(string forbidden)
    {
        foreach (var allowPrivate in new[] { false, true })
        {
            var guard = new UrlGuard(new ConfigurationBuilder().Build(), NullLogger<UrlGuard>.Instance,
                _ => [IPAddress.Parse("2606:4700:4700::1111"), IPAddress.Parse(forbidden)]);

            var error = Assert.Throws<InvalidOperationException>(
                () => guard.EnsureSafe("https://test.invalid", allowPrivate));
            Assert.Contains("SSRF blocked", error.Message);
        }
    }

    // The deprecated IPv4-compatible form (::a.b.c.d) is not an IPv4-mapped address, so it
    // used to skip every IPv4 rule. It can name loopback, the metadata endpoint or a private
    // host, so the whole ::/96 block is refused.
    [Theory]
    [InlineData("::127.0.0.1")]
    [InlineData("::7f00:1")]
    [InlineData("::169.254.169.254")]
    [InlineData("::10.0.0.1")]
    [InlineData("::192.168.1.1")]
    [InlineData("::203.0.113.10")]
    public void Blocks_ipv4_compatible_ipv6_addresses_with_either_allow_private_value(string address)
    {
        foreach (var allowPrivate in new[] { false, true })
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Guard().EnsureSafe($"http://[{address}]", allowPrivate));
            Assert.Contains("SSRF blocked", error.Message);
        }
    }

    // Zone identifiers do not change what an address is. Whether the URL parser accepts the
    // form or rejects it outright, the request is refused.
    [Theory]
    [InlineData("http://[fec0::1%25eth0]/")]
    [InlineData("http://[fec0::1%eth0]/")]
    [InlineData("http://[fe80::1%25eth0]/")]
    [InlineData("http://[fd00::1%25eth0]/")]
    public void Zone_identifiers_do_not_bypass_the_ipv6_local_range_checks(string url)
    {
        foreach (var allowPrivate in new[] { false, true })
            Assert.Throws<InvalidOperationException>(() => Guard().EnsureSafe(url, allowPrivate));
    }

    // IPv4-mapped addresses are judged by their IPv4 rules, as before: private only with
    // the opt-in, metadata and loopback never.
    [Fact]
    public void Ipv4_mapped_private_addresses_follow_the_ipv4_allow_private_rule()
    {
        Assert.Throws<InvalidOperationException>(() => Guard().EnsureSafe("http://[::ffff:10.0.0.1]", false));
        Guard().EnsureSafe("http://[::ffff:10.0.0.1]", true);
        Assert.Throws<InvalidOperationException>(() => Guard().EnsureSafe("http://[::ffff:169.254.169.254]", true));
        Assert.Throws<InvalidOperationException>(() => Guard().EnsureSafe("http://[::ffff:127.0.0.1]", true));
    }

    // NAT64 (64:ff9b::/96) is how every IPv4-only host looks on an IPv6-only network with
    // DNS64, so the prefix itself passes; the IPv4 address it carries gets the IPv4 rules.
    [Theory]
    [InlineData("64:ff9b::127.0.0.1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b::169.254.169.254")]
    [InlineData("64:ff9b::0.0.0.1")]
    public void Nat64_addresses_embedding_loopback_metadata_or_reserved_ipv4_are_refused(string address)
    {
        foreach (var allowPrivate in new[] { false, true })
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Guard().EnsureSafe($"http://[{address}]", allowPrivate));
            Assert.Contains("SSRF blocked", error.Message);
        }
    }

    [Theory]
    [InlineData("64:ff9b::10.0.0.1")]
    [InlineData("64:ff9b::172.16.0.1")]
    [InlineData("64:ff9b::192.168.1.1")]
    [InlineData("64:ff9b::100.64.0.1")]
    public void Nat64_addresses_embedding_private_ipv4_follow_the_allow_private_rule(string address)
    {
        Assert.Throws<InvalidOperationException>(() => Guard().EnsureSafe($"http://[{address}]", false));
        Guard().EnsureSafe($"http://[{address}]", true);
    }

    [Fact]
    public void Nat64_addresses_embedding_a_public_ipv4_pass()
    {
        Guard().EnsureSafe("https://[64:ff9b::203.0.113.10]", false);
        Guard().EnsureSafe("https://[64:ff9b::203.0.113.10]", true);
    }

    [Fact]
    public void A_nat64_dns_answer_embedding_loopback_is_refused_even_after_a_public_one()
    {
        var guard = new UrlGuard(new ConfigurationBuilder().Build(), NullLogger<UrlGuard>.Instance,
            _ => [IPAddress.Parse("64:ff9b::203.0.113.10"), IPAddress.Parse("64:ff9b::127.0.0.1")]);

        var error = Assert.Throws<InvalidOperationException>(() => guard.EnsureSafe("https://test.invalid"));
        Assert.Contains("SSRF blocked", error.Message);
    }

    // 6to4 and Teredo are deprecated tunnelling forms that carry an arbitrary IPv4 target
    // for a relay to reach, so like IPv4-compatible they are refused outright.
    [Theory]
    [InlineData("2002:7f00:1::1")]
    [InlineData("2002:a9fe:a9fe::1")]
    [InlineData("2002:cb00:710a::1")]
    [InlineData("2001::1")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    public void Blocks_6to4_and_teredo_addresses_with_either_allow_private_value(string address)
    {
        foreach (var allowPrivate in new[] { false, true })
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Guard().EnsureSafe($"http://[{address}]", allowPrivate));
            Assert.Contains("SSRF blocked", error.Message);
        }
    }

    // 100.64.0.0/10 is carrier-grade NAT space: private, so open only with the opt-in.
    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.100.100.200")]
    [InlineData("100.127.255.254")]
    public void Cgnat_addresses_are_private(string address)
    {
        Assert.Throws<InvalidOperationException>(() => Guard().EnsureSafe($"http://{address}", false));
        Guard().EnsureSafe($"http://{address}", true);
    }

    [Theory]
    [InlineData("100.63.255.254")]
    [InlineData("100.128.0.1")]
    public void Addresses_just_outside_cgnat_are_not_private(string address)
        => Guard().EnsureSafe($"http://{address}", false);

    // The quiet path judges the same way and still writes nothing.
    [Theory]
    [InlineData("http://100.64.0.1")]
    [InlineData("http://[64:ff9b::10.0.0.1]")]
    [InlineData("http://[2002:7f00:1::1]")]
    public void The_new_refusals_are_quiet_on_the_quiet_path(string url)
    {
        var log = new RedirectTargetRefusalTests.CapturingLogger<UrlGuard>();
        var guard = new UrlGuard(new ConfigurationBuilder().Build(), log, _ => []);

        Assert.False(guard.IsAllowedQuietly(url));
        Assert.Equal("", log.Text);
    }

    [Theory]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:db8::1")]
    public void Public_ipv6_addresses_pass_with_either_allow_private_value(string address)
    {
        Guard().EnsureSafe($"https://[{address}]", false);
        Guard().EnsureSafe($"https://[{address}]", true);
    }

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
