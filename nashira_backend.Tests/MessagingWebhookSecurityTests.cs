using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Messaging;
using nashira_backend.Services.Messaging.Providers;
using nashira_backend.Services.Security;

namespace nashira_backend.Tests;

public class MessagingWebhookSecurityTests
{
    [Fact]
    public async Task Unsigned_slack_ingest_cannot_create_events_or_jobs()
    {
        using var services = Services();
        var channelId = await SeedChannelAsync(services, "slack", allowUnsigned: true);
        var request = new MessagingHttpRequest
        {
            Method = "POST",
            Body = System.Text.Encoding.UTF8.GetBytes("""
                {"type":"event_callback","event_id":"unsigned-test","event":{
                  "type":"app_mention","user":"Utest","channel":"Ctest","ts":"1","text":"test"}}
                """),
        };

        var result = await Ingest(services).ReceiveAsync("slack", channelId, request, default);

        Assert.Equal(401, result.StatusCode);
        await AssertNoEffectsAsync(services);
    }

    [Theory]
    [InlineData("slack")]
    [InlineData("telegram")]
    [InlineData("teams")]
    [InlineData("unknown")]
    public async Task Get_without_a_provider_handshake_does_not_reveal_channels(string provider)
    {
        using var services = Services();
        var channelId = await SeedChannelAsync(services, "slack");
        var ingest = Ingest(services);
        var request = new MessagingHttpRequest { Method = "GET", Body = [] };

        var existing = await ingest.ReceiveAsync(provider, channelId, request, default);
        var missing = await ingest.ReceiveAsync(provider, Guid.NewGuid(), request, default);

        Assert.Equal(405, existing.StatusCode);
        Assert.Equal(missing, existing);
        await AssertNoEffectsAsync(services);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect")]
    public async Task WhatsApp_get_without_the_verify_token_does_not_reveal_channels(string? token)
    {
        using var services = Services();
        var channelId = await SeedChannelAsync(services, "whatsapp");
        var otherId = await SeedChannelAsync(services, "slack");
        var ingest = Ingest(services);
        var request = WhatsAppChallenge(token);

        var existing = await ingest.ReceiveAsync("whatsapp", channelId, request, default);
        var missing = await ingest.ReceiveAsync("whatsapp", Guid.NewGuid(), request, default);
        var mismatch = await ingest.ReceiveAsync("whatsapp", otherId, request, default);

        Assert.Equal(401, existing.StatusCode);
        Assert.Equal(missing, existing);
        Assert.Equal(mismatch, existing);
        await AssertNoEffectsAsync(services);
    }

    [Fact]
    public async Task WhatsApp_get_with_the_verify_token_preserves_the_challenge()
    {
        using var services = Services();
        var channelId = await SeedChannelAsync(services, "whatsapp");

        var outcome = await Ingest(services).ReceiveAsync(
            "whatsapp", channelId, WhatsAppChallenge("test-verify-token"), default);

        Assert.Equal(new MessagingIngestOutcome(200, "challenge-value", Raw: true), outcome);
        await AssertNoEffectsAsync(services);
    }

    private static MessagingHttpRequest WhatsAppChallenge(string? token)
    {
        var query = new Dictionary<string, string>
        {
            ["hub.mode"] = "subscribe",
            ["hub.challenge"] = "challenge-value",
        };
        if (token is not null) query["hub.verify_token"] = token;
        return new MessagingHttpRequest { Method = "GET", Body = [], Query = query };
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        var database = $"messaging-webhook-security-{Guid.NewGuid()}";
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddOptions<MessagingOptions>();
        services.AddSingleton<ISecretProtector, TestProtector>();
        services.AddSingleton<IMessagingLinkService, NoLinks>();
        services.AddSingleton<IMessagingProviderResolver>(new MessagingProviderResolver(
        [
            new SlackProvider(new NoHttp(), NullLogger<SlackProvider>.Instance),
            new WhatsAppProvider(new NoHttp(), NullLogger<WhatsAppProvider>.Instance),
        ]));
        return services.BuildServiceProvider();
    }

    private static MessagingIngestService Ingest(ServiceProvider services) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<MessagingIngestService>.Instance);

    private static async Task<Guid> SeedChannelAsync(ServiceProvider services, string provider, bool allowUnsigned = false)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        db.MessagingChannels.Add(new MessagingChannel
        {
            MessagingChannelId = id, Provider = provider, IsActive = true, Enabled = true,
            AllowUnsigned = allowUnsigned, RequireLinkedUser = false,
            ExternalConfigJson = """{"verify_token":"test-verify-token"}""",
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task AssertNoEffectsAsync(ServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Jobs.ToListAsync());
        Assert.Empty(await db.MessagingInboundEvents.ToListAsync());
        Assert.Empty(await db.AIConversations.ToListAsync());
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Unexpected HTTP call");
    }

    private sealed class TestProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext) => throw new NotSupportedException();
        public string? Decrypt(byte[]? ciphertext) => null;
    }

    private sealed class NoLinks : IMessagingLinkService
    {
        public Task<LinkInvite> CreateLinkAsync(MessagingChannel channel, string? workspace, string user, CancellationToken ct)
            => throw new InvalidOperationException("Unexpected account-link creation");
        public Task<LinkPreview> PreviewAsync(string token, CancellationToken ct) => throw new NotSupportedException();
        public Task<Guid> ConfirmAsync(string token, CancellationToken ct) => throw new NotSupportedException();
    }
}
