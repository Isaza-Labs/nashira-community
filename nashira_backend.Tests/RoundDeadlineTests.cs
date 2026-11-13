using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Ai.Providers;
using nashira_backend.Services.Ai.Skills;
using nashira_backend.Services.Ai.Tools;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;
using nashira_backend.Services.Trace;

namespace nashira_backend.Tests;

// The providers stop reading a stream when the turn's token is cancelled without
// throwing, so a deadline that passes while an event is being handled used to end the
// round as if the model had finished: the partial text became the answer and the turn
// was recorded as completed. It is a timeout.
public class RoundDeadlineTests
{
    [Fact]
    public async Task A_deadline_that_passes_while_text_is_being_sent_ends_the_turn_as_a_timeout()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"round-deadline-{Guid.NewGuid()}").Options);
        db.AIProviders.Add(new AIProvider
        {
            AIProviderId = Guid.NewGuid(),
            Name = "ring",
            Type = "custom",
            BaseURL = "https://ring.example/v1",
            DefaultModel = "m",
            EncryptedApiKey = Encoding.UTF8.GetBytes("k"),
            Config = JsonDocument.Parse("{}").RootElement,
            Enabled = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Two text chunks and a normal end. The deadline passes while the first chunk
        // is with the browser, so the second is never read.
        const string body =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hal\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"f an answer\"},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n\n";
        var user = new SignedInUser();
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var runner = new AgentConversationRunner(
            db,
            new LlmProviderFactory(db, user, new Http(body), new PassthroughProtector(), NullLoggerFactory.Instance),
            registry,
            // Only ApproveTools is reached: the model calls no tool in this turn.
            new ToolDispatcher(registry, null!, user, null!, null!, null!, null!, null!,
                NullLogger<ToolDispatcher>.Instance, new NoopTrace()),
            new Prompt(),
            new ScopedSkillCatalog(db, null!),
            new UserProfileContextLoader(db, user),
            user,
            new AgentTurnScope(),
            NullLogger<AgentConversationRunner>.Instance,
            new NoopTrace(),
            Options.Create(new AiChatOptions { StreamDeadlineSeconds = 1 }));
        var sink = new SlowSink(TimeSpan.FromMilliseconds(1_500));

        await runner.RunAsync(new AgentTurnRequest(null, "hello"), sink, CancellationToken.None);

        Assert.Equal(["Hal"], sink.Text);
        Assert.Equal("timeout", sink.ErrorCode);
        Assert.False(sink.Done);
        var turn = Assert.Single(await db.AgentTurns.ToListAsync());
        Assert.Equal(AgentTurn.StatusTimeout, turn.Status);
    }

    private sealed class SlowSink(TimeSpan textDelay) : IAgentEventSink
    {
        public List<string> Text { get; } = [];
        public string? ErrorCode { get; private set; }
        public bool Done { get; private set; }

        public Task ConversationAsync(Guid conversationId, bool isNew, CancellationToken ct) => Task.CompletedTask;
        public Task ModelAsync(Guid providerId, string providerName, string model, CancellationToken ct) => Task.CompletedTask;
        public async Task TextAsync(string delta, CancellationToken ct)
        {
            Text.Add(delta);
            await Task.Delay(textDelay, CancellationToken.None);
        }
        public Task ToolStartAsync(string name, JsonElement args, CancellationToken ct) => Task.CompletedTask;
        public Task ToolResultAsync(string name, ToolCallOutput output, CancellationToken ct) => Task.CompletedTask;
        public Task ConfirmationRequiredAsync(string name, JsonElement args, string tier, CancellationToken ct) => Task.CompletedTask;
        public Task DoneAsync(int tokensIn, int tokensOut, int cacheRead, int iterations, CancellationToken ct)
        {
            Done = true;
            return Task.CompletedTask;
        }
        public Task ErrorAsync(string message, string? code, CancellationToken ct)
        {
            ErrorCode = code;
            return Task.CompletedTask;
        }
    }

    private sealed class Prompt : ISkillPromptLoader
    {
        public Task<string> LoadAsync(string toolList, CancellationToken ct) => Task.FromResult("base");
        public Task<IReadOnlyList<BuiltinSkill>> ListBuiltinsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BuiltinSkill>>([]);
        public Task<bool> SaveBuiltinAsync(string name, string content, CancellationToken ct) => Task.FromResult(false);
        public void Invalidate() { }
    }

    private sealed class Http(string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Canned(body));

        private sealed class Canned(string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
                });
        }
    }

    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext) =>
            plaintext is null ? null : Encoding.UTF8.GetBytes(plaintext);

        public string? Decrypt(byte[]? ciphertext) =>
            ciphertext is null || ciphertext.Length == 0 ? null : Encoding.UTF8.GetString(ciphertext);
    }

    private sealed class SignedInUser : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string? Username => "tester";
        public IReadOnlyList<string> Roles => ["admin"];
        public bool IsAuthenticated => true;
    }

    private sealed class NoopTrace : ITraceLogger
    {
        public void Event(string category, string action, object? metadata = null,
            string? error = null, int? durationMs = null) { }

        public ITraceScope Begin(string category, string action, object? metadata = null) => new Scope();

        private sealed class Scope : ITraceScope
        {
            public void Complete(object? metadata = null) { }
            public void Fail(string error, object? metadata = null) { }
            public void Dispose() { }
        }
    }
}
