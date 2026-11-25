using nashira_backend.Data.Models;
using nashira_backend.Services.Security;

namespace nashira_backend.Tests;

// A credential's scope: the hosts device SSH may send it to. Parsing is strict (a
// typo must not silently widen or empty a scope) and matching is literal: no DNS,
// no family crossing, nothing a scope does not list.
public class CredentialScopeTests
{
    // ─── Normalize ───────────────────────────────────────────────────────

    [Fact]
    public void Blank_input_is_unscoped()
    {
        Assert.Null(CredentialScope.Normalize(null));
        Assert.Null(CredentialScope.Normalize(""));
        Assert.Null(CredentialScope.Normalize("  \n , \t "));
    }

    [Fact]
    public void Entries_split_on_commas_newlines_and_whitespace_trimmed_and_deduplicated_in_order()
    {
        var normalized = CredentialScope.Normalize(" 10.0.0.9,\r\nCore-SW.lab.example  10.0.0.9\nfd00::/8 core-sw.lab.example ");

        Assert.Equal("10.0.0.9\ncore-sw.lab.example\nfd00::/8", normalized);
    }

    [Theory]
    [InlineData("10.0.0.77/24", "10.0.0.0/24")]
    [InlineData("10.1.2.3/8", "10.0.0.0/8")]
    [InlineData("10.0.0.9/32", "10.0.0.9")]
    [InlineData("fd00:1:2::5/48", "fd00:1:2::/48")]
    [InlineData("fd00::1/128", "fd00::1")]
    [InlineData("::ffff:10.0.0.9", "10.0.0.9")]
    [InlineData("FD00::ABCD", "fd00::abcd")]
    [InlineData("router1.example.", "router1.example")]
    public void Entries_are_stored_canonical(string raw, string expected)
    {
        Assert.Equal(expected, CredentialScope.Normalize(raw));
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.0.0.0/0")]
    [InlineData("fd00::/0")]
    public void A_scope_that_covers_everything_is_refused(string raw)
    {
        var ex = Assert.Throws<CredentialScope.ScopeFormatException>(() => CredentialScope.Normalize(raw));
        Assert.Contains(raw, ex.Message);
    }

    [Theory]
    [InlineData("*.example.com")]
    [InlineData("10.0.0.0/33")]
    [InlineData("fd00::/129")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/abc")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("/24")]
    [InlineData("10.0.0")]
    [InlineData("10")]
    [InlineData("256.1.1.1")]
    [InlineData("010.0.0.1")]
    [InlineData("fe80::1%eth0")]
    [InlineData("fd00:::1")]
    [InlineData("::ffff:10.0.0.0/120")]
    [InlineData("http://host")]
    [InlineData("-bad-.example")]
    public void Garbage_is_refused_naming_the_bad_entry(string bad)
    {
        var ex = Assert.Throws<CredentialScope.ScopeFormatException>(
            () => CredentialScope.Normalize($"10.0.0.1, {bad}"));
        Assert.Contains($"'{bad}'", ex.Message);

        Assert.False(CredentialScope.TryNormalize(bad, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.Contains(bad, error);
    }

    // ─── Covers ──────────────────────────────────────────────────────────

    [Fact]
    public void Unscoped_covers_anything()
    {
        Assert.True(CredentialScope.Covers(null, "203.0.113.1"));
        Assert.True(CredentialScope.Covers("", "anything"));
        Assert.True(CredentialScope.Permits(new Credential(), "198.51.100.1"));
        Assert.False(CredentialScope.IsScoped(new Credential()));
        Assert.True(CredentialScope.IsScoped(new Credential { AllowedHosts = "10.0.0.1" }));
    }

    [Theory]
    [InlineData("10.0.0.9", "10.0.0.9", true)]
    [InlineData("10.0.0.9", " 10.0.0.9 ", true)]
    [InlineData("10.0.0.9", "10.0.0.10", false)]
    [InlineData("10.0.0.0/24", "10.0.0.0", true)]
    [InlineData("10.0.0.0/24", "10.0.0.255", true)]
    [InlineData("10.0.0.0/24", "10.0.1.0", false)]
    [InlineData("10.0.0.0/24", "9.255.255.255", false)]
    [InlineData("10.0.0.128/25", "10.0.0.127", false)]
    [InlineData("10.0.0.128/25", "10.0.0.128", true)]
    [InlineData("10.0.0.0/24", "::ffff:10.0.0.7", true)]
    [InlineData("10.0.0.9", "::ffff:10.0.0.9", true)]
    [InlineData("fd00::/8", "fd12:3456::1", true)]
    [InlineData("fd00::/8", "fe80::1", false)]
    [InlineData("fd00::1", "FD00:0:0::1", true)]
    public void Addresses_match_exact_ips_and_cidr_networks(string scope, string host, bool expected)
    {
        Assert.Equal(expected, CredentialScope.Covers(scope, host));
    }

    [Fact]
    public void An_ipv4_host_never_matches_an_ipv6_network_and_vice_versa()
    {
        // ::/96 would contain the IPv4-compatible form of every IPv4 address.
        Assert.False(CredentialScope.Covers("::/96", "10.0.0.1"));
        Assert.False(CredentialScope.Covers("::ffff:0:0/96", "10.0.0.1"));
        Assert.False(CredentialScope.Covers("10.0.0.0/8", "::a00:1"));
        Assert.False(CredentialScope.Covers("10.0.0.0/8", "fd00::10.0.0.1"));
    }

    [Theory]
    [InlineData("core-sw.lab.example", "core-sw.lab.example", true)]
    [InlineData("core-sw.lab.example", "CORE-SW.Lab.Example", true)]
    [InlineData("core-sw.lab.example", "core-sw.lab.example.", true)]
    [InlineData("core-sw.lab.example", "x.core-sw.lab.example", false)]
    [InlineData("lab.example", "core-sw.lab.example", false)]
    [InlineData("core-sw", "core-sw.lab.example", false)]
    public void Hostnames_match_exactly_and_case_insensitively(string scope, string host, bool expected)
    {
        Assert.Equal(expected, CredentialScope.Covers(scope, host));
    }

    [Fact]
    public void Names_and_addresses_never_cross_and_nothing_resolves()
    {
        // "localhost" would resolve to 127.0.0.1; a scope listing the address does not
        // cover the name, and a scope listing the name does not cover the address.
        Assert.False(CredentialScope.Covers("127.0.0.1", "localhost"));
        Assert.False(CredentialScope.Covers("localhost", "127.0.0.1"));
    }

    [Fact]
    public void A_scoped_credential_covers_nothing_blank_and_a_damaged_row_fails_closed()
    {
        Assert.False(CredentialScope.Covers("10.0.0.0/24", ""));
        Assert.False(CredentialScope.Covers("10.0.0.0/24", null));
        Assert.False(CredentialScope.Covers("not a scope!!", "10.0.0.1"));
        Assert.False(CredentialScope.Covers("0.0.0.0/0", "10.0.0.1"));
    }

    [Fact]
    public void The_out_of_scope_message_names_credential_and_device_but_not_the_scope()
    {
        var message = CredentialScope.OutOfScopeMessage("ops-ssh", "core-sw");
        Assert.Contains("ops-ssh", message);
        Assert.Contains("core-sw", message);
        Assert.Equal("credential_out_of_scope", CredentialScope.OutOfScopeCode);
    }
}
