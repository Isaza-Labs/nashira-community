using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Trace;

namespace nashira_backend.BackgroundServices;

// Drains the trace queue into the database in batches.
//
// Its own scope and its own DbContext, never the request's: a trace row landing in the
// context a controller is using would join that controller's change tracker, and a
// failure writing it would poison every later save in the same request — the exact bug
// the audit logger had to be fixed for.
//
// A start and its completion share an id, so this is an upsert. When both halves arrive
// in the same batch they collapse into one insert, which means a fast operation never
// persists a `started` row at all and only slow or stuck work leaves one visible. That
// is the shape worth reading: a screen full of `started` rows is a screen full of
// problems.
public sealed class TraceWriterHostedService : BackgroundService
{
    // Long enough to batch usefully, short enough that a live tail feels live.
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
    private const int MaxBatch = 500;

    private readonly TraceLogger _source;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TraceWriterHostedService> _logger;

    private long _reportedDrops;

    public TraceWriterHostedService(
        ITraceLogger source, IServiceScopeFactory scopes, ILogger<TraceWriterHostedService> logger)
    {
        // The interface deliberately has no Reader: producing and draining are different
        // jobs, and only this one is allowed to consume.
        _source = (TraceLogger)source;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<TraceEvent>(MaxBatch);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await DrainAsync(batch, stoppingToken);
        }

        // Shutdown: one last pass so a clean stop does not throw away what is already
        // queued. CancellationToken.None because the token that got us here is already
        // cancelled and the write would refuse to run.
        await DrainAsync(batch, CancellationToken.None);
    }

    private async Task DrainAsync(List<TraceEvent> batch, CancellationToken ct)
    {
        while (true)
        {
            batch.Clear();
            while (batch.Count < MaxBatch && _source.Reader.TryRead(out var row)) batch.Add(row);
            if (batch.Count == 0) break;

            try
            {
                await WriteAsync(batch, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The batch is lost. Deliberately not retried: the trail exists to
                // explain failures, and a writer that stalls retrying its own is the
                // one component whose outage nothing else will report.
                _logger.LogError(ex, "trace.write.failed dropped={Count}", batch.Count);
            }

            ReportDrops();
            if (batch.Count < MaxBatch) break;
        }
    }

    private async Task WriteAsync(IReadOnlyList<TraceEvent> batch, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Last write per id wins: a completion supersedes the start it closes.
        var latest = batch
            .GroupBy(r => r.TraceEventId)
            .ToDictionary(g => g.Key, g => g.Last());

        var ids = latest.Keys.ToList();
        var existing = await db.TraceEvents
            .Where(t => ids.Contains(t.TraceEventId))
            .ToListAsync(ct);

        foreach (var row in existing)
        {
            var update = latest[row.TraceEventId];
            row.Status = update.Status;
            row.DurationMs = update.DurationMs;
            row.ErrorMessage = update.ErrorMessage;
            row.MetadataJson = update.MetadataJson;
            row.UpdatedAt = update.UpdatedAt;
            latest.Remove(row.TraceEventId);
        }

        if (latest.Count > 0) db.TraceEvents.AddRange(latest.Values);
        await db.SaveChangesAsync(ct);
    }

    // Said once per new drop rather than per dropped row: a queue that overflowed is
    // one fact, and repeating it for each of four thousand rows would itself become
    // the noise it is reporting.
    private void ReportDrops()
    {
        var dropped = _source.DroppedCount;
        if (dropped <= _reportedDrops) return;
        _logger.LogWarning(
            "trace.queue.overflow dropped_total={Dropped} — the writer is behind and the "
            + "oldest traces were discarded", dropped);
        _reportedDrops = dropped;
    }
}
