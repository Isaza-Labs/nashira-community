using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using nashira_backend.Services.Net;

namespace nashira_backend.Tests;

// The address in a redirect is chosen by the remote server, so its host, path and query can carry
// anything. Judging it must leave no trace of it in the URL guard's own log, at any level, nor in
// the refusal. These cases use the real guard and a logger on the guard itself, with DNS answered
// by an injected resolver (no network).
public class RedirectTargetLoggingTests
{
    private const string HostMarker = "example-host-marker";
    private const string PathMarker = "example-path-marker";
    private const string QueryMarker = "example-query-marker";

    private static readonly string Target =
        $"https://{HostMarker}.example.net/{PathMarker}?q={QueryMarker}";

    private static readonly string[] Markers = [HostMarker, PathMarker, QueryMarker];

    private static (UrlGuard Guard, RedirectTargetRefusalTests.CapturingLogger<UrlGuard> Log) Guard(
        Func<string, IPAddress[]> resolve)
    {
        var log = new RedirectTargetRefusalTests.CapturingLogger<UrlGuard>();
        return (new UrlGuard(new ConfigurationBuilder().Build(), log, resolve), log);
    }

    private static async Task<RedirectRefusedException> RedirectedAsync(UrlGuard guard, string location)
    {
        var factory = new RedirectRefusalMappingTests.RedirectingFactory(HttpStatusCode.Found, location, guard);
        using var client = factory.CreateClient("example");
        return await Assert.ThrowsAsync<RedirectRefusedException>(
            () => client.GetAsync("https://203.0.113.50/start"));
    }

    private static void AssertNoTrace(RedirectRefusedException refusal, string logged)
    {
        foreach (var marker in Markers)
        {
            Assert.DoesNotContain(marker, logged);
            Assert.DoesNotContain(marker, refusal.ToString());
        }
        Assert.Equal("", logged);
    }

    [Fact]
    public async Task A_target_that_does_not_resolve_is_refused_and_not_logged()
    {
        var (guard, log) = Guard(_ => throw new SocketException((int)SocketError.HostNotFound));

        var refusal = await RedirectedAsync(guard, Target);

        Assert.Equal(RedirectRefusalReason.TargetNotAllowed, refusal.Reason);
        Assert.Equal(302, refusal.StatusCode);
        AssertNoTrace(refusal, log.Text);
    }

    [Fact]
    public async Task A_target_that_resolves_to_a_private_address_is_refused_and_not_logged()
    {
        var (guard, log) = Guard(_ => [IPAddress.Parse("10.0.0.7")]);

        var refusal = await RedirectedAsync(guard, Target);

        Assert.Equal(RedirectRefusalReason.TargetNotAllowed, refusal.Reason);
        AssertNoTrace(refusal, log.Text);
    }

    [Fact]
    public async Task A_blocked_literal_host_is_refused_and_its_path_and_query_are_not_logged()
    {
        var (guard, log) = Guard(_ => throw new InvalidOperationException("not reached"));

        var refusal = await RedirectedAsync(guard, $"http://127.0.0.1/{PathMarker}?q={QueryMarker}");

        Assert.Equal(RedirectRefusalReason.TargetNotAllowed, refusal.Reason);
        AssertNoTrace(refusal, log.Text);
    }

    [Fact]
    public async Task An_acceptable_target_is_still_not_followed_and_not_logged()
    {
        var (guard, log) = Guard(_ => [IPAddress.Parse("203.0.113.60")]);

        var refusal = await RedirectedAsync(guard, Target);

        Assert.Equal(RedirectRefusalReason.Disabled, refusal.Reason);
        AssertNoTrace(refusal, log.Text);
    }

    [Fact]
    public async Task A_relative_target_is_judged_without_logging_too()
    {
        var (guard, log) = Guard(_ => [IPAddress.Parse("203.0.113.60")]);

        var refusal = await RedirectedAsync(guard, $"/{PathMarker}?q={QueryMarker}");

        Assert.Equal(RedirectRefusalReason.Disabled, refusal.Reason);
        AssertNoTrace(refusal, log.Text);
    }

    [Theory]
    [InlineData("10.0.0.7", false)]
    [InlineData("fd00::1", false)]
    [InlineData("203.0.113.60", true)]
    public void The_quiet_check_gives_the_same_answer_as_the_guard_and_logs_nothing(string address, bool allowed)
    {
        var (guard, log) = Guard(_ => [IPAddress.Parse(address)]);

        Assert.Equal(allowed, guard.IsAllowedQuietly(Target));
        Assert.Equal(allowed, Throws(() => guard.EnsureSafe(Target)) is null);
        Assert.Equal(allowed, guard.IsAllowedQuietly(Target, allowPrivate: false));
        Assert.False(guard.IsAllowedQuietly("not a url"));
        Assert.False(guard.IsAllowedQuietly("ftp://203.0.113.60/x"));

        // Only the guard's own EnsureSafe call wrote anything: a fresh guard that makes just that call
        // writes the same lines.
        var (reference, referenceLog) = Guard(_ => [IPAddress.Parse(address)]);
        Throws(() => reference.EnsureSafe(Target));
        Assert.Equal(CountLines(referenceLog.Text, "net.url_guard"), CountLines(log.Text, "net.url_guard"));
    }

    // The caller's own configured target keeps its existing logging.
    [Fact]
    public void A_configured_target_is_still_logged_by_the_guard_as_before()
    {
        var (guard, log) = Guard(_ => [IPAddress.Parse("10.0.0.7")]);

        Assert.Throws<InvalidOperationException>(() => guard.EnsureSafe("https://example-configured.example.net/x"));

        Assert.Contains("net.url_guard.blocked host=example-configured.example.net reason=private_address", log.Text);
    }

    private static Exception? Throws(Action act)
    {
        try { act(); return null; } catch (Exception ex) { return ex; }
    }

    private static int CountLines(string text, string needle) =>
        text.Split('\n').Count(l => l.Contains(needle, StringComparison.Ordinal));
}
