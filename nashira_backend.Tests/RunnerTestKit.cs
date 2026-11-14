using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Ai.Permissions;
using nashira_backend.Services.Ai.Providers;
using nashira_backend.Services.Ai.Skills;
using nashira_backend.Services.Ai.Tools;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;
using nashira_backend.Services.Trace;

namespace nashira_backend.Tests;

// A real AgentConversationRunner over a real provider adapter, with the model server
// replaced by scripted response bodies and the database by EF InMemory. No tool is
// registered, so a tool call the script makes comes back as "unknown tool": enough to
// drive a second round without the services a real tool needs.
internal sealed class RunnerTestKit : IAsyncDisposable
{
    public AppDbContext Db { get; }
    public ScriptedHttp Http { get; }
    public RecordingSink Sink { get; }
    public RunnerLog Log { get; } = new();
    public AgentConversationRunner Runner { get; }

    public RunnerTestKit(
        ScriptedHttp http, RecordingSink? sink = null, int deadlineSeconds = 240, string providerType = "custom",
        ICurrentUser? caller = null)
    {
        Http = http;
        Sink = sink ?? new RecordingSink();
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"runner-kit-{Guid.NewGuid()}").Options);
        Db.AIProviders.Add(new AIProvider
        {
            AIProviderId = Guid.NewGuid(),
            Name = "model-server",
            Type = providerType,
            BaseURL = "https://model.example/v1",
            DefaultModel = "m",
            EncryptedApiKey = Encoding.UTF8.GetBytes("k"),
            Config = JsonDocument.Parse("{}").RootElement,
            Enabled = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();

        var user = caller ?? new SignedInUser();
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        Runner = new AgentConversationRunner(
            Db,
            new LlmProviderFactory(Db, user, http, new PassthroughProtector(), NullLoggerFactory.Instance),
            registry,
            // An unregistered tool is answered before the guard, autonomy, audit and
            // self-correction services are reached, so those stay null.
            new ToolDispatcher(registry, null!, user,
                new PermissionClassifier(NullLogger<PermissionClassifier>.Instance), null!, null!, null!, null!,
                NullLogger<ToolDispatcher>.Instance, new NoopTrace()),
            new Prompt(),
            new ScopedSkillCatalog(Db, null!),
            new UserProfileContextLoader(Db, user),
            user,
            new AgentTurnScope(),
            Log,
            new NoopTrace(),
            Options.Create(new AiChatOptions { StreamDeadlineSeconds = deadlineSeconds }));
    }

    public Task RunAsync(string message = "hello", CancellationToken ct = default) =>
        Runner.RunAsync(new AgentTurnRequest(null, message), Sink, ct);

    public async Task<AgentTurn> StoredTurnAsync() => Assert.Single(await Db.AgentTurns.ToListAsync());

    public ValueTask DisposeAsync() => Db.DisposeAsync();

    // One server-sent-events body from its `data:` payloads.
    public static string Sse(params string[] data) =>
        string.Concat(data.Select(d => $"data: {d}\n\n"));

    private sealed class Prompt : ISkillPromptLoader
    {
        public Task<string> LoadAsync(string toolList, CancellationToken ct) => Task.FromResult("base");
        public Task<IReadOnlyList<BuiltinSkill>> ListBuiltinsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BuiltinSkill>>([]);
        public Task<bool> SaveBuiltinAsync(string name, string content, CancellationToken ct) => Task.FromResult(false);
        public void Invalidate() { }
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

// What the model server sends back, one body per request in order. A request beyond
// the script fails the turn, so a test that expects no further round sees it.
internal sealed class ScriptedHttp(params ScriptedBody[] bodies) : IHttpClientFactory
{
    private int _calls;
    public int Calls => _calls;

    public HttpClient CreateClient(string name) => new(new Handler(this));

    private HttpResponseMessage Next()
    {
        var index = Interlocked.Increment(ref _calls) - 1;
        if (index >= bodies.Length)
            throw new InvalidOperationException($"the script has no response for request {index + 1}");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(bodies[index]) };
    }

    private sealed class Handler(ScriptedHttp owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(owner.Next());
    }
}

// A response body that sends its text and then closes, breaks, or goes silent with the
// connection open until the read is cancelled.
internal sealed class ScriptedBody(string text, ScriptedBody.Ending ending = ScriptedBody.Ending.Close) : Stream
{
    public enum Ending { Close, Break, Hang }

    private readonly byte[] _bytes = Encoding.UTF8.GetBytes(text);
    private int _position;

    public static implicit operator ScriptedBody(string text) => new(text);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_position < _bytes.Length)
        {
            var count = Math.Min(buffer.Length, _bytes.Length - _position);
            _bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        switch (ending)
        {
            case Ending.Break:
                throw new IOException("the connection was reset");
            case Ending.Hang:
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            default:
                return 0;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// What the browser would have been sent. The two delays stand in for a slow client:
// they do not watch the turn's token, as a write that is already under way does not.
internal sealed class RecordingSink(TimeSpan textDelay = default, TimeSpan toolResultDelay = default) : IAgentEventSink
{
    public List<string> Text { get; } = [];
    public List<string> ToolResults { get; } = [];
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool Errored { get; private set; }
    public bool Done { get; private set; }
    public (int TokensIn, int TokensOut, int CacheRead, int Iterations) Totals { get; private set; }

    public Task ConversationAsync(Guid conversationId, bool isNew, CancellationToken ct) => Task.CompletedTask;
    public Task ModelAsync(Guid providerId, string providerName, string model, CancellationToken ct) => Task.CompletedTask;
    public async Task TextAsync(string delta, CancellationToken ct)
    {
        Text.Add(delta);
        if (textDelay > TimeSpan.Zero) await Task.Delay(textDelay, CancellationToken.None);
    }
    public Task ToolStartAsync(string name, JsonElement args, CancellationToken ct) => Task.CompletedTask;
    public async Task ToolResultAsync(string name, ToolCallOutput output, CancellationToken ct)
    {
        ToolResults.Add(name);
        if (toolResultDelay > TimeSpan.Zero) await Task.Delay(toolResultDelay, CancellationToken.None);
    }
    public Task ConfirmationRequiredAsync(string name, JsonElement args, string tier, CancellationToken ct) => Task.CompletedTask;
    public Task DoneAsync(int tokensIn, int tokensOut, int cacheRead, int iterations, CancellationToken ct)
    {
        Done = true;
        Totals = (tokensIn, tokensOut, cacheRead, iterations);
        return Task.CompletedTask;
    }
    public Task ErrorAsync(string message, string? code, CancellationToken ct)
    {
        Errored = true;
        ErrorMessage = message;
        ErrorCode = code;
        return Task.CompletedTask;
    }
}

// The runner's log lines as written, so a test can read the per-round line.
internal sealed class RunnerLog : ILogger<AgentConversationRunner>
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Rounds
    {
        get { lock (_messages) return _messages.Where(m => m.StartsWith("ai.chat.iteration ")).ToList(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_messages) _messages.Add(formatter(state, exception));
    }
}
