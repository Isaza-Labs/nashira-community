using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Providers;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;

namespace nashira_backend.Tests;

// A clock that moves only when the test moves it. A timer made on it fires inside
// Advance, on the test's own thread, when the clock reaches the time it was set for:
// a test can stop one millisecond short of a limit and then step over it.
internal sealed class ManualTime : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _now;

    // How many timers were ever made on this clock.
    public int Created { get; private set; }

    // How many of them have not been disposed.
    public int Live
    {
        get { lock (_gate) return _timers.Count; }
    }

    // How far from now each timer that is set will fire, soonest first.
    public IReadOnlyList<TimeSpan> Due
    {
        get { lock (_gate) return _timers.Where(t => t.At is not null).Select(t => t.At!.Value - _now).Order().ToList(); }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate) return _now.Ticks;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return DateTimeOffset.UnixEpoch + _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("one-shot timers only");
        lock (_gate)
        {
            Created++;
            var timer = new ManualTimer(this, callback, state);
            timer.Set(dueTime);
            _timers.Add(timer);
            return timer;
        }
    }

    public void Advance(TimeSpan by)
    {
        TimeSpan until;
        lock (_gate) until = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.At <= until).MinBy(t => t.At);
                if (next is null)
                {
                    _now = until;
                    return;
                }
                _now = next.At!.Value;
                next.At = null;
            }
            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTime clock, TimerCallback callback, object? state) : ITimer
    {
        // When it fires, on the clock; null while it is not set.
        public TimeSpan? At;

        public void Set(TimeSpan dueTime) =>
            At = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("one-shot timers only");
            lock (clock._gate)
            {
                if (!clock._timers.Contains(this)) return false;
                Set(dueTime);
                return true;
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

// A response body the test holds: the reader gets exactly the bytes the test feeds it,
// the test can wait until the reader has taken them all and is waiting for more, and it
// can see which cancellation token that waiting read is under. Nothing here depends on
// how long anything takes.
internal sealed class GatedBody : Stream
{
    private readonly object _gate = new();
    private readonly List<byte> _fed = [];
    private bool _ended;
    private Exception? _broken;
    private TaskCompletionSource<int>? _read;
    private Memory<byte> _readBuffer;
    private CancellationTokenRegistration _readCancel;
    private TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Runs when the read that is waiting is cancelled, before that read ends.
    public Action? OnReadCancelled { get; set; }

    // The token of the latest read: once Waiting() has completed, of the read that waits.
    public CancellationToken ReadToken { get; private set; }

    public int ReadsCancelled { get; private set; }
    public bool Disposed { get; private set; }

    // Completes when the reader has taken everything fed so far and asked for more.
    public Task Waiting()
    {
        lock (_gate) return _waiting.Task;
    }

    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    public void Feed(byte[] bytes)
    {
        TaskCompletionSource<int>? read;
        var count = 0;
        lock (_gate)
        {
            _fed.AddRange(bytes);
            read = TakeWaitingRead();
            if (read is not null) count = Take(_readBuffer.Span);
        }
        read?.TrySetResult(count);
    }

    // The server closes the stream: the reader reaches its end.
    public void End()
    {
        TaskCompletionSource<int>? read;
        lock (_gate)
        {
            _ended = true;
            read = TakeWaitingRead();
        }
        read?.TrySetResult(0);
    }

    // The connection fails.
    public void Break(Exception error)
    {
        TaskCompletionSource<int>? read;
        lock (_gate)
        {
            _broken = error;
            read = TakeWaitingRead();
        }
        read?.TrySetException(error);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        TaskCompletionSource<int> read;
        lock (_gate)
        {
            ReadToken = ct;
            if (_fed.Count > 0) return new(Take(buffer.Span));
            if (_broken is not null) return ValueTask.FromException<int>(_broken);
            if (_ended) return new(0);
            read = _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _readBuffer = buffer;
        }
        // Outside the lock: for a token that is already cancelled this runs at once.
        var registration = ct.UnsafeRegister(_ => Cancelled(read, ct), null);
        lock (_gate)
        {
            if (_read == read)
            {
                _readCancel = registration;
                _waiting.TrySetResult();
            }
            else
            {
                registration.Unregister();
            }
        }
        return new(read.Task);
    }

    private void Cancelled(TaskCompletionSource<int> read, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_read != read) return;
            TakeWaitingRead();
            ReadsCancelled++;
        }
        OnReadCancelled?.Invoke();
        read.TrySetCanceled(ct);
    }

    private TaskCompletionSource<int>? TakeWaitingRead()
    {
        if (_read is not { } read) return null;
        _read = null;
        _readCancel.Unregister();
        _readCancel = default;
        _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return read;
    }

    private int Take(Span<byte> into)
    {
        var count = Math.Min(into.Length, _fed.Count);
        for (var i = 0; i < count; i++) into[i] = _fed[i];
        _fed.RemoveRange(0, count);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        TaskCompletionSource<int>? read;
        lock (_gate)
        {
            Disposed = true;
            read = TakeWaitingRead();
        }
        read?.TrySetException(new ObjectDisposedException(nameof(GatedBody)));
        base.Dispose(disposing);
    }

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

// A model server that answers each request with the next scripted response and keeps
// the request bodies it was sent. A request beyond the script fails, so a test that
// expects no further request sees one.
internal sealed class GatedServer : IHttpClientFactory
{
    private readonly object _gate = new();
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    private readonly List<string> _requests = [];

    public IReadOnlyList<string> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    public GatedServer Then(GatedBody body)
    {
        _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        return this;
    }

    public GatedServer Then(HttpStatusCode status, string body)
    {
        _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body) });
        return this;
    }

    public HttpClient CreateClient(string name = "") => new(new Handler(this));

    private sealed class Handler(GatedServer owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var sent = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (owner._gate)
            {
                owner._requests.Add(sent);
                if (owner._responses.Count == 0)
                    throw new InvalidOperationException($"the script has no response for request {owner._requests.Count}");
                return owner._responses.Dequeue()();
            }
        }
    }
}

// What each kind of provider puts on the wire for a reply of "hola": what it may send
// before the model has generated anything, the text itself, and the rest of the reply.
internal sealed record IdleWire(string Opening, string Said, string Rest)
{
    public const string AnthropicStart =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5}}}\n\n"
        + "event: ping\ndata: {\"type\":\"ping\"}\n\n";

    public const string AnthropicText =
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hola\"}}\n\n";

    public const string AnthropicEnd =
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
        + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n"
        + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    public const string OpenAiRole = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n";

    public static readonly string[] Types = ["custom", "anthropic", "gemini", "ollama"];

    public static TheoryData<string> EveryType => new(Types);

    public static IdleWire For(string type) => type switch
    {
        "custom" => new(
            OpenAiRole,
            "data: {\"choices\":[{\"delta\":{\"content\":\"hola\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"),
        "anthropic" => new(AnthropicStart, AnthropicText, AnthropicEnd),
        "gemini" => new(
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"\"}]}}]}\n\n",
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"hola\"}]}}]}\n\n",
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"\"}]},\"finishReason\":\"STOP\"}]}\n\n"),
        "ollama" => new(
            "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":false}\n",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"hola\"},\"done\":false}\n",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"prompt_eval_count\":3,\"eval_count\":1}\n"),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    // The adapter for a provider type with the limit given, as its own constructor takes it.
    public static IStreamingToolCallingLlmProvider Provider(string type, HttpClient http, TimeSpan limit) => type switch
    {
        "custom" => new OpenAiProvider(http, "k", "https://model.example", providerType: "custom", streamIdle: limit),
        "anthropic" => new AnthropicProvider(http, "k", "https://model.example", streamIdle: limit),
        "gemini" => new GeminiProvider(http, "k", "https://model.example", streamIdle: limit),
        "ollama" => new OllamaProvider(http, "", "http://model.example", streamIdle: limit),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}

// The provider a chat turn would get for a stored row: built by LlmProviderFactory from
// the row's Config and the server-wide setting.
internal static class StoredIdleProvider
{
    public static async Task<IStreamingToolCallingLlmProvider> ResolveAsync(
        string type, string config, IHttpClientFactory http, int serverWideSeconds = 0)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"stream-idle-{Guid.NewGuid()}").Options);
        var id = Guid.NewGuid();
        db.AIProviders.Add(new AIProvider
        {
            AIProviderId = id,
            Name = "p",
            Type = type,
            BaseURL = "https://model.example",
            DefaultModel = "m",
            EncryptedApiKey = Encoding.UTF8.GetBytes("k"),
            Config = JsonDocument.Parse(config).RootElement,
        });
        await db.SaveChangesAsync();
        var factory = new LlmProviderFactory(
            db, new NoUser(), http, new PlainProtector(), NullLoggerFactory.Instance,
            Options.Create(new AiChatOptions { StreamIdleSeconds = serverWideSeconds }));
        return (await factory.ResolveAsync(id, null, CancellationToken.None)).Provider;
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext) => plaintext is null ? null : Encoding.UTF8.GetBytes(plaintext);
        public string? Decrypt(byte[]? ciphertext) =>
            ciphertext is null || ciphertext.Length == 0 ? null : Encoding.UTF8.GetString(ciphertext);
    }

    private sealed class NoUser : ICurrentUser
    {
        public Guid UserId => throw new InvalidOperationException("anonymous");
        public string? Username => null;
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => false;
    }
}
