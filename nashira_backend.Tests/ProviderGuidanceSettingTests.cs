using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Ai.Providers;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;

namespace nashira_backend.Tests;

// Config.guidance_after_question on a stored provider row, read the way a chat turn
// reads it: resolved by LlmProviderFactory, then decided by the runner's gate.
public class ProviderGuidanceSettingTests
{
    [Theory]
    [InlineData("""{"guidance_after_question":true}""", false, true)]
    [InlineData("""{"guidance_after_question":false}""", true, false)]
    [InlineData("""{"models":["m2"]}""", true, true)]
    [InlineData("""{"models":["m2"]}""", false, false)]
    [InlineData("""{"guidance_after_question":"yes"}""", true, true)]
    public async Task A_stored_rows_setting_reaches_the_runners_decision(string config, bool switchOn, bool expected)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"provider-guidance-{Guid.NewGuid()}").Options);
        var id = Guid.NewGuid();
        db.AIProviders.Add(new AIProvider
        {
            AIProviderId = id,
            Name = "ring",
            Type = "custom",
            BaseURL = "https://ring.example/v1",
            DefaultModel = "m",
            EncryptedApiKey = Encoding.UTF8.GetBytes("k"),
            Config = JsonDocument.Parse(config).RootElement,
        });
        await db.SaveChangesAsync();
        var factory = new LlmProviderFactory(
            db, new AnonymousUser(), new Http(), new PassthroughProtector(), NullLoggerFactory.Instance);

        var resolved = await factory.ResolveAsync(id, null, CancellationToken.None);

        Assert.IsType<OpenAiProvider>(resolved.Provider);
        Assert.Equal(expected, AgentConversationRunner.ScopedPromptAfterUserFor(
            switchOn, resolved.GuidanceAfterQuestion, resolved.Provider));
    }

    [Fact]
    public async Task An_anthropic_row_with_the_setting_on_still_keeps_the_guidance_ahead()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"provider-guidance-{Guid.NewGuid()}").Options);
        var id = Guid.NewGuid();
        db.AIProviders.Add(new AIProvider
        {
            AIProviderId = id,
            Name = "claude",
            Type = "anthropic",
            DefaultModel = "m",
            EncryptedApiKey = Encoding.UTF8.GetBytes("k"),
            Config = JsonDocument.Parse("""{"guidance_after_question":true}""").RootElement,
        });
        await db.SaveChangesAsync();
        var factory = new LlmProviderFactory(
            db, new AnonymousUser(), new Http(), new PassthroughProtector(), NullLoggerFactory.Instance);

        var resolved = await factory.ResolveAsync(id, null, CancellationToken.None);

        Assert.True(resolved.GuidanceAfterQuestion);
        Assert.False(AgentConversationRunner.ScopedPromptAfterUserFor(true, resolved.GuidanceAfterQuestion, resolved.Provider));
    }

    private sealed class Http : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext) =>
            plaintext is null ? null : Encoding.UTF8.GetBytes(plaintext);

        public string? Decrypt(byte[]? ciphertext) =>
            ciphertext is null || ciphertext.Length == 0 ? null : Encoding.UTF8.GetString(ciphertext);
    }

    private sealed class AnonymousUser : ICurrentUser
    {
        public Guid UserId => throw new InvalidOperationException("anonymous");
        public string? Username => null;
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => false;
    }
}
