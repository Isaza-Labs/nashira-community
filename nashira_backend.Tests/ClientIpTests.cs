using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using nashira_backend.Services.Observability;
using nashira_backend.Services.Security;

namespace nashira_backend.Tests;

// Where the client address comes from — the login rate limiter's partition key
// and the audit/auth-event trails all depend on it, and the trust decision is
// security-sensitive in both directions (trust too little: one shared login
// budget for the whole platform; trust too much: anyone can forge its origin).
public class ClientIpTests
{
    private static ForwardedHeadersOptions Options(string? trustedProxies, int? forwardLimit = null)
    {
        var settings = new Dictionary<string, string?>();
        if (trustedProxies is not null) settings["Network:TrustedProxies"] = trustedProxies;
        if (forwardLimit is not null) settings["Network:ForwardLimit"] = forwardLimit.Value.ToString();

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddForwardedHeadersFromConfig(config);
        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    }

    // Runs the real middleware so the test exercises the trust decision
    // rather than re-implementing it.
    private static async Task<string?> ResolveThroughMiddlewareAsync(
        string? trustedProxies, IPAddress peer, string? forwardedFor, int? forwardLimit = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = peer;
        if (forwardedFor is not null) ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;

        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(Options(trustedProxies, forwardLimit)));

        await middleware.Invoke(ctx);
        return ClientIp.Resolve(ctx);
    }

    // The whole point of the trust list: an untrusted caller cannot forge its
    // own address. Without this, anyone could dodge the login rate limiter and
    // write fabricated origins into the audit log.
    [Fact]
    public async Task ClientIp_IgnoresForwardedForFromAnUntrustedPeer()
    {
        var ip = await ResolveThroughMiddlewareAsync(
            trustedProxies: "172.29.0.0/16",
            peer: IPAddress.Parse("203.0.113.7"),   // outside the trusted range
            forwardedFor: "1.2.3.4");

        Assert.Equal("203.0.113.7", ip);
    }

    [Fact]
    public async Task ClientIp_HonoursForwardedForFromATrustedNetwork()
    {
        var ip = await ResolveThroughMiddlewareAsync(
            trustedProxies: "172.29.0.0/16",
            peer: IPAddress.Parse("172.29.0.5"),    // the frontend container
            forwardedFor: "1.2.3.4");

        Assert.Equal("1.2.3.4", ip);
    }

    [Fact]
    public async Task ClientIp_HonoursForwardedForFromAnIndividuallyTrustedProxy()
    {
        var ip = await ResolveThroughMiddlewareAsync(
            trustedProxies: "10.1.2.3",
            peer: IPAddress.Parse("10.1.2.3"),
            forwardedFor: "1.2.3.4");

        Assert.Equal("1.2.3.4", ip);
    }

    // Unset => trust nothing. Wrong (the address is the proxy) but not
    // forgeable, which is the right default before an operator has described
    // their topology.
    //
    // This is the trap the feature has to be built around: to
    // ForwardedHeadersMiddleware, an empty trust list means "skip the check" —
    // trust EVERYONE — not "trust nobody". Clearing the lists while leaving
    // the feature enabled would have made any caller able to forge its own
    // address, which is strictly worse than the bug being fixed. So an empty
    // trust list disables the middleware outright.
    [Fact]
    public async Task ClientIp_TrustsNothingWhenUnconfigured()
    {
        var ip = await ResolveThroughMiddlewareAsync(
            trustedProxies: null,
            peer: IPAddress.Parse("172.29.0.5"),
            forwardedFor: "1.2.3.4");

        Assert.Equal("172.29.0.5", ip);
    }

    [Fact]
    public void ForwardedHeaders_AreDisabledOutrightWhenNothingIsTrusted()
    {
        Assert.Equal(ForwardedHeaders.None, Options(trustedProxies: null).ForwardedHeaders);
        // Every entry unparseable is the same situation as none at all.
        Assert.Equal(ForwardedHeaders.None, Options("not-an-ip").ForwardedHeaders);
    }

    [Fact]
    public void ForwardedHeaders_AreEnabledOnlyWithATrustList()
    {
        var options = Options("172.29.0.0/16");

        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
    }

    [Fact]
    public void ForwardedHeaders_ClearsTheLoopbackOnlyDefaults()
    {
        var options = Options(trustedProxies: null);

        // The framework default trusts loopback, which silently disagrees with
        // "TrustedProxies is unset means trust nothing".
        Assert.Empty(options.KnownIPNetworks);
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public void ForwardedHeaders_ParsesAMixedCidrAndHostList()
    {
        var options = Options("172.29.0.0/16, 10.1.2.3 ,10.1.2.4");

        Assert.Single(options.KnownIPNetworks);
        Assert.Equal(2, options.KnownProxies.Count);
    }

    // A typo must not stop the process booting — logging the wrong IP beats
    // not starting at all. The entry is dropped, the rest still apply.
    [Fact]
    public void ForwardedHeaders_DropsUnparseableEntriesWithoutThrowing()
    {
        var options = Options("not-an-ip, 172.29.0.0/99, 172.29.0.0/16");

        Assert.Single(options.KnownIPNetworks);
        Assert.Empty(options.KnownProxies);
    }

    // System.Net.IPNetwork rejects a network address with host bits set, but an
    // operator writing "172.29.0.1/16" clearly means the /16 it sits in.
    [Fact]
    public void ForwardedHeaders_MasksHostBitsInsteadOfDroppingTheEntry()
    {
        var options = Options("172.29.0.1/16");

        var network = Assert.Single(options.KnownIPNetworks);
        Assert.Equal(IPAddress.Parse("172.29.0.0"), network.BaseAddress);
        Assert.True(network.Contains(IPAddress.Parse("172.29.200.7")));
    }

    [Fact]
    public void ForwardedHeaders_DefaultsToASingleHopAndHonoursAnOverride()
    {
        Assert.Equal(1, Options("172.29.0.0/16").ForwardLimit);
        Assert.Equal(2, Options("172.29.0.0/16", forwardLimit: 2).ForwardLimit);
    }

    [Fact]
    public void ForwardedHeaders_DescribeReportsWhetherAnythingIsTrusted()
    {
        var none = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()).Build();
        Assert.Contains("none", ForwardedHeadersConfiguration.Describe(none));

        var some = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Network:TrustedProxies"] = "172.29.0.0/16" }).Build();
        Assert.Equal("172.29.0.0/16", ForwardedHeadersConfiguration.Describe(some));
    }

    // Kestrel reports IPv4 peers in mapped form on a dual-stack socket. Two
    // spellings of one client would mean two rate-limit partitions and two
    // shapes in the audit log.
    [Fact]
    public void ClientIp_NormalisesIpv6MappedIpv4()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:10.0.0.5");

        Assert.Equal("10.0.0.5", ClientIp.Resolve(ctx));
    }

    [Fact]
    public void ClientIp_ResolveOrUnknownNeverReturnsNull()
    {
        Assert.Equal("unknown", ClientIp.ResolveOrUnknown(null));
        Assert.Equal("unknown", ClientIp.ResolveOrUnknown(new DefaultHttpContext()));
    }
}
