namespace nashira_backend.Services.Ai.Providers;

// A model's stream that went silent part-way through, before it said it was done.
//
// The message reaches the person in the chat, after whatever text had already
// streamed, so it says what happened in their terms rather than the socket's.
public sealed class LlmStreamStalledException(TimeSpan limit)
    : Exception($"The model stopped sending for {limit.TotalSeconds:0} s before finishing its reply, so the reply may be cut off. Try again.")
{
    public TimeSpan Limit { get; } = limit;
}

// Reads a provider's response stream line by line and ends it when it goes silent.
//
// The turn deadline (AiChat:StreamDeadlineSeconds) is the only other bound on a
// stream, and the "llm" HttpClient's Timeout stops applying once the response
// headers have arrived (the providers read with ResponseHeadersRead). So a server
// that stops sending part-way through a reply without closing the connection held
// the turn until the deadline: measured with DeepSeek-V4.1 on TensorFold, a reply
// the server had finished in seven seconds sat for four minutes because its last
// lines never arrived.
//
// The limit is on silence, and only once the model has started its reply:
//
//  - It starts when the provider calls ReplyStarted, on the first thing the model
//    generated (answer text, reasoning or a piece of a tool call). Whatever a server
//    sends before that does not start it: an opening chunk that only names the role,
//    an `event:` line, a comment. A server that answers at once with such a line and
//    then spends minutes reading a long prompt is bounded by the turn deadline alone,
//    as it was before this existed.
//  - Any bytes reset it, a whole line or not. A line that arrives in pieces is a
//    stream that is alive, and so is one that sends keepalive comments while the
//    model is busy (TensorFold sends one after 15 s of silence).
internal sealed class StreamLineReader : IDisposable
{
    // Process-wide default, set once at startup from AiChat:StreamIdleSeconds.
    // Providers take their own value so tests never have to change this one.
    internal static TimeSpan DefaultLimit { get; set; } = TimeSpan.FromSeconds(AiChatDefaults.StreamIdleSeconds);

    private readonly StreamReader _reader;
    private readonly TimeSpan _limit;
    private bool _replyStarted;

    // The timer of the read in flight, while the limit applies; bytes push it back.
    private CancellationTokenSource? _idle;

    public StreamLineReader(Stream stream, TimeSpan limit)
    {
        _limit = limit;
        _reader = new StreamReader(new ReadNotifyingStream(stream, BytesArrived));
    }

    // The model has generated something: from here on silence is limited.
    public void ReplyStarted() => _replyStarted = true;

    // The next line, or null at the end of the stream. Throws
    // LlmStreamStalledException when a started reply sends nothing for the limit;
    // cancellation of `ct` itself still surfaces as OperationCanceledException.
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        if (!_replyStarted || _limit <= TimeSpan.Zero)
            return await _reader.ReadLineAsync(ct);

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(_limit);
        _idle = idle;
        try
        {
            return await _reader.ReadLineAsync(idle.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmStreamStalledException(_limit);
        }
        finally
        {
            _idle = null;
        }
    }

    private void BytesArrived()
    {
        // A read that has just been cancelled or has just returned needs no more time.
        try { _idle?.CancelAfter(_limit); }
        catch (ObjectDisposedException) { }
    }

    // Disposes the response stream with it, as the StreamReader it replaces did.
    public void Dispose() => _reader.Dispose();

    // The response stream, telling the reader each time bytes come out of it.
    private sealed class ReadNotifyingStream(Stream inner, Action bytesArrived) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Seen(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Seen(inner.Read(buffer));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => Seen(await inner.ReadAsync(buffer.AsMemory(offset, count), ct));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => Seen(await inner.ReadAsync(buffer, ct));

        private int Seen(int read)
        {
            if (read > 0) bytesArrived();
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

// Defaults shared by AiChatOptions and the providers, so neither restates the other.
internal static class AiChatDefaults
{
    public const int StreamIdleSeconds = 60;
}
