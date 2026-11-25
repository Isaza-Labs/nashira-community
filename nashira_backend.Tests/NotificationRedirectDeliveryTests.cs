using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Net;
using nashira_backend.Services.Notifications;
using nashira_backend.Services.Security;

namespace nashira_backend.Tests;

// A guarded client refuses to follow a redirect by throwing. For a notification that is a
// failed delivery like any other: it is recorded, the channel's health says so, and the
// caller gets a result instead of an exception.
public class NotificationRedirectDeliveryTests
{
    private const string WebhookUrl = "https://203.0.113.10/hooks/example-webhook-token";

    [Fact]
    public async Task A_refused_redirect_is_recorded_as_a_failed_delivery_and_updates_channel_health()
    {
        var (dispatcher, db, transport, channelId) = await Build(HttpStatusCode.Found, "https://203.0.113.20/elsewhere");

        var result = await dispatcher.SendAsync(channelId, "example message", null, default);

        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
        // A redirect does not clear on a retry, so there is exactly one attempt.
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, transport.Requests);
        Assert.Contains("redirect refused", result.Error);

        var delivery = await db.NotificationDeliveries.SingleAsync();
        Assert.False(delivery.Success);
        Assert.Equal(1, delivery.Attempts);
        Assert.Equal(result.Error, delivery.Error);

        var channel = await db.NotificationChannels.SingleAsync();
        Assert.Equal(Integration.StatusUnreachable, channel.Status);
        Assert.Equal(result.Error, channel.LastCheckError);
        Assert.NotNull(channel.LastCheckedAt);
    }

    [Theory]
    [InlineData("https://203.0.113.20/elsewhere")]
    [InlineData(null)]
    public async Task The_recorded_failure_never_carries_the_webhook_url_or_the_remote_location(string? location)
    {
        var (dispatcher, db, _, channelId) = await Build(HttpStatusCode.TemporaryRedirect, location);

        var result = await dispatcher.SendAsync(channelId, "example message", null, default);

        foreach (var text in new[]
                 {
                     result.Error, (await db.NotificationDeliveries.SingleAsync()).Error,
                     (await db.NotificationChannels.SingleAsync()).LastCheckError,
                 })
        {
            Assert.DoesNotContain("example-webhook-token", text);
            Assert.DoesNotContain("203.0.113.20", text);
        }
    }

    [Theory]
    [InlineData("http://[fd00::1]/next?t=example-location-token")]
    [InlineData("ftp://203.0.113.50/next?t=example-location-token")]
    public async Task A_rejected_redirect_target_is_still_recorded_as_a_refused_redirect_without_the_location(string location)
    {
        var guard = new UrlGuard(new ConfigurationBuilder().Build(), NullLogger<UrlGuard>.Instance);
        var (dispatcher, db, transport, channelId) = await Build(HttpStatusCode.Found, location, guard);

        var result = await dispatcher.SendAsync(channelId, "example message", null, default);

        Assert.False(result.Success);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, transport.Requests);
        Assert.Contains("redirect refused", result.Error);
        foreach (var text in new[] { result.Error, (await db.NotificationDeliveries.SingleAsync()).Error })
        {
            Assert.DoesNotContain("example-location-token", text);
            Assert.DoesNotContain("example-webhook-token", text);
        }
    }

    [Fact]
    public async Task A_send_failure_that_is_not_a_redirect_is_not_reported_as_one_and_hides_its_message()
    {
        var (dispatcher, db, _, channelId) = await Build(
            HttpStatusCode.OK, null, transportError: new InvalidOperationException($"example failure for {WebhookUrl}"));

        var result = await dispatcher.SendAsync(channelId, "example message", null, default);

        Assert.False(result.Success);
        Assert.DoesNotContain("redirect", result.Error);
        Assert.DoesNotContain("example-webhook-token", result.Error);
        Assert.DoesNotContain("example-webhook-token", (await db.NotificationDeliveries.SingleAsync()).Error);
    }

    private static async Task<(NotificationDispatcher, AppDbContext, Transport, Guid)> Build(
        HttpStatusCode status, string? location, IUrlGuard? redirectGuard = null, Exception? transportError = null)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"notification-redirect-{Guid.NewGuid()}").Options);
        var protector = new PlainProtector();
        var channelId = Guid.NewGuid();
        db.NotificationChannels.Add(new NotificationChannel
        {
            NotificationChannelId = channelId, Name = "example-channel", Slug = "example-channel",
            Kind = NotificationChannel.KindWebhook, WebhookUrlEncrypted = protector.Encrypt(WebhookUrl), Enabled = true,
        });
        await db.SaveChangesAsync();

        var transport = new Transport(status, location, transportError);
        var guard = new AllowAllGuard();
        var client = new HttpClient(new RejectRedirectHandler(redirectGuard ?? guard) { InnerHandler = transport });
        var dispatcher = new NotificationDispatcher(
            db, protector, guard, new Factory(client), NullLogger<NotificationDispatcher>.Instance);
        return (dispatcher, db, transport, channelId);
    }

    private sealed class Transport(HttpStatusCode status, string? location, Exception? error = null) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (error is not null) throw error;
            var response = new HttpResponseMessage(status);
            if (location is not null) response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        }
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class AllowAllGuard : IUrlGuard
    {
        public void EnsureSafe(string url, bool allowPrivate = false) { }
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? value) => value is null ? null : Encoding.UTF8.GetBytes(value);
        public string? Decrypt(byte[]? value) => value is null ? null : Encoding.UTF8.GetString(value);
    }
}
