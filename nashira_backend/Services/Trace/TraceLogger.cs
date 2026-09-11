using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using nashira_backend.Data.Models;
using nashira_backend.Services.Audit;

namespace nashira_backend.Services.Trace;

// Buffered, never-blocking trace writer.
//
// Rows go into a bounded in-memory queue and are written in batches by
// TraceWriterHostedService. Flow-weaver inserts each row inline; at this scope — a row
// on every HTTP request, plus start and completion for every job and tool call — that
// would put one or two extra database round-trips on the critical path of everything
// the platform does, to record that the platform did it.
//
// Three consequences, all deliberate:
//
//  - The queue is bounded and drops the OLDEST row when full. A burst that outruns the
//    writer loses the beginning of the burst rather than its end, and the end is where
//    the failure is. Unbounded would trade a lost debugging row for an OOM.
//  - Nothing here awaits, and nothing here throws. A trail that can fail the operation
//    it describes is worse than no trail: the whole point is to observe without
//    participating.
//  - Rows buffered at the moment of a hard kill are lost. Acceptable — this is the
//    debugging trail, not the evidence. What must survive is in audit_events, which is
//    written synchronously and chained.
public sealed class TraceLogger : ITraceLogger
{
    // Roughly a second of very heavy traffic. Past that, the writer is not keeping up
    // and the oldest rows are the least interesting.
    private const int Capacity = 4096;
    private const int MaxMetadataChars = 4096;
    private const int MaxErrorChars = 1024;

    private readonly Channel<TraceEvent> _queue;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<TraceLogger> _logger;

    private long _dropped;

    public TraceLogger(IHttpContextAccessor http, ILogger<TraceLogger> logger)
    {
        _http = http;
        _logger = logger;
        _queue = Channel.CreateBounded<TraceEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<TraceEvent> Reader => _queue.Reader;

    /// <summary>How many rows the queue has thrown away. Reported by the writer.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void Event(string category, string action, object? metadata = null, string? error = null,
        int? durationMs = null)
    {
        var row = NewRow(category, action, metadata);
        row.Status = error is null ? TraceEvent.StatusCompleted : TraceEvent.StatusFailed;
        row.ErrorMessage = Clip(error, MaxErrorChars);
        // Zero, not null: an instantaneous event was measured and took no time, where
        // null is reserved for "started and still running".
        row.DurationMs = durationMs ?? 0;
        Enqueue(row);
    }

    public ITraceScope Begin(string category, string action, object? metadata = null)
    {
        var started = NewRow(category, action, metadata);
        started.Status = TraceEvent.StatusStarted;
        Enqueue(started);
        return new Scope(this, started);
    }

    private TraceEvent NewRow(string category, string action, object? metadata)
    {
        var ctx = _http.HttpContext;
        // Parsed here rather than through ICurrentUser: this is a singleton, and
        // resolving a scoped service per row would allocate a scope on every trace.
        var claim = ctx?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userId = Guid.TryParse(claim, out var id) ? id : (Guid?)null;

        return new TraceEvent
        {
            TraceEventId = Guid.NewGuid(),
            Category = category,
            Action = action,
            UserId = userId,
            // The signed-in name, or the automation that bound itself — the same
            // convention the audit trail uses, so "who" reads alike in both tables.
            Actor = ctx?.User.FindFirstValue(ClaimTypes.Name) ?? AuditActor.Current,
            RequestId = ctx?.TraceIdentifier,
            MetadataJson = Serialize(metadata),
            At = DateTime.UtcNow,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private void Enqueue(TraceEvent row)
    {
        // DropOldest means TryWrite only fails once the channel is completed, which
        // happens at shutdown. Counting it keeps the writer honest about the gap.
        if (!_queue.Writer.TryWrite(row)) Interlocked.Increment(ref _dropped);
    }

    private string? Serialize(object? metadata)
    {
        if (metadata is null) return null;
        try
        {
            return Clip(JsonSerializer.Serialize(metadata), MaxMetadataChars);
        }
        catch (Exception ex)
        {
            // A cyclic or unserialisable object must not take down the operation being
            // traced. The row still goes in, saying why it is bare.
            _logger.LogDebug(ex, "trace.metadata.unserialisable");
            return "{\"_error\":\"metadata could not be serialised\"}";
        }
    }

    private static string? Clip(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    // The two rows of a Begin/Complete pair are joined by sharing an id: the completion
    // is an UPDATE of the started row, so a stuck operation leaves exactly one row that
    // says "started" instead of a pair the reader has to match up by eye.
    private sealed class Scope : ITraceScope
    {
        private readonly TraceLogger _owner;
        private readonly TraceEvent _started;
        private readonly long _stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        private bool _closed;

        public Scope(TraceLogger owner, TraceEvent started)
        {
            _owner = owner;
            _started = started;
        }

        public void Complete(object? metadata = null) =>
            Close(TraceEvent.StatusCompleted, null, metadata);

        public void Fail(string error, object? metadata = null) =>
            Close(TraceEvent.StatusFailed, error, metadata);

        // Disposing without a verdict means the scope was left through an exception.
        // Recorded as failed: assuming success here would turn every crash into a clean
        // run in the one table that exists to show otherwise.
        public void Dispose() =>
            Close(TraceEvent.StatusFailed, "the operation did not report an outcome", null);

        private void Close(string status, string? error, object? metadata)
        {
            if (_closed) return;
            _closed = true;

            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_stamp);
            var row = new TraceEvent
            {
                // Same id: the writer treats a repeat as the completion of the row it
                // already queued, so the pair is one row in the end.
                TraceEventId = _started.TraceEventId,
                Category = _started.Category,
                Action = _started.Action,
                UserId = _started.UserId,
                Actor = _started.Actor,
                RequestId = _started.RequestId,
                Status = status,
                DurationMs = (int)Math.Min(int.MaxValue, elapsed.TotalMilliseconds),
                ErrorMessage = Clip(error, MaxErrorChars),
                MetadataJson = metadata is null ? _started.MetadataJson : _owner.Serialize(metadata),
                At = _started.At,
                IsActive = true,
                CreatedAt = _started.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
            };
            _owner.Enqueue(row);
        }
    }
}
