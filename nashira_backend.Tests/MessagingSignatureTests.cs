using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Models;
using nashira_backend.Services.Messaging;
using nashira_backend.Services.Messaging.Providers;

namespace nashira_backend.Tests;

public class MessagingSignatureTests
{
    [Theory]
    [InlineData("slack", null)]
    [InlineData("slack", "")]
    [InlineData("telegram", null)]
    [InlineData("telegram", "")]
    [InlineData("whatsapp", null)]
    [InlineData("whatsapp", "")]
    [InlineData("teams", null)]
    public async Task AllowUnsigned_cannot_bypass_provider_authentication(string provider, string? secret)
    {
        var channel = new MessagingChannel { Provider = provider, AllowUnsigned = true };
        var request = new MessagingHttpRequest { Method = "POST", Body = "{}"u8.ToArray() };

        var result = await Provider(provider).VerifyAsync(channel, request, secret, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Equal(401, result.RejectStatusCode);
    }

    [Theory]
    [InlineData("slack")]
    [InlineData("telegram")]
    [InlineData("whatsapp")]
    public async Task Signed_requests_still_pass_and_tampering_fails(string provider)
    {
        const string secret = "test-signing-secret";
        var body = "{}"u8.ToArray();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (provider)
        {
            case "slack":
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                headers["X-Slack-Request-Timestamp"] = timestamp;
                headers["X-Slack-Signature"] = "v0=" + Hmac(secret, Encoding.UTF8.GetBytes($"v0:{timestamp}:{{}}"));
                break;
            case "telegram":
                headers["X-Telegram-Bot-Api-Secret-Token"] = secret;
                break;
            case "whatsapp":
                headers["X-Hub-Signature-256"] = "sha256=" + Hmac(secret, body);
                break;
        }
        var channel = new MessagingChannel { Provider = provider, AllowUnsigned = true };
        var request = new MessagingHttpRequest { Method = "POST", Body = body, Headers = headers };
        var handler = Provider(provider);

        Assert.Equal(WebhookVerifyOutcome.Verified,
            (await handler.VerifyAsync(channel, request, secret, default)).Outcome);
        Assert.Equal(WebhookVerifyOutcome.Rejected,
            (await handler.VerifyAsync(channel, request, "different-secret", default)).Outcome);
    }

    [Fact]
    public async Task Signed_slack_url_verification_still_returns_its_challenge()
    {
        const string secret = "test-signing-secret";
        const string json = """{"type":"url_verification","challenge":"challenge-value"}""";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var request = new MessagingHttpRequest
        {
            Method = "POST", Body = Encoding.UTF8.GetBytes(json),
            Headers = new Dictionary<string, string>
            {
                ["X-Slack-Request-Timestamp"] = timestamp,
                ["X-Slack-Signature"] = "v0=" + Hmac(secret, Encoding.UTF8.GetBytes($"v0:{timestamp}:{json}")),
            },
        };

        var result = await Provider("slack").VerifyAsync(new MessagingChannel(), request, secret, default);

        Assert.Equal(WebhookVerifyOutcome.Challenge, result.Outcome);
        Assert.Equal("challenge-value", result.ChallengeBody);
    }

    private static string Hmac(string secret, byte[] body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    private static IMessagingProvider Provider(string provider) => provider switch
    {
        "slack" => new SlackProvider(new NoHttp(), NullLogger<SlackProvider>.Instance),
        "telegram" => new TelegramProvider(new NoHttp(), NullLogger<TelegramProvider>.Instance),
        "whatsapp" => new WhatsAppProvider(new NoHttp(), NullLogger<WhatsAppProvider>.Instance),
        "teams" => new TeamsProvider(new NoHttp(), NullLogger<TeamsProvider>.Instance),
        _ => throw new ArgumentException(nameof(provider)),
    };

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Unexpected HTTP call");
    }
}
