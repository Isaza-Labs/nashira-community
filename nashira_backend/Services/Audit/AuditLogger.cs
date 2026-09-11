using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Observability;

namespace nashira_backend.Services.Audit;

// Writes hash-chained AuditEvent rows. A single semaphore serializes writes
// so Sequence + PrevHash stay consistent (single-process; a multi-replica
// deployment needs a distributed lock or a DB sequence — see Phase 5.5).
public sealed class AuditLogger : IAuditLogger
{
    // Serializes chain appends process-wide: Sequence and PrevHash must be read
    // and written atomically or two concurrent writes would fork the chain.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(AppDbContext db, ICurrentUser user, IHttpContextAccessor http, ILogger<AuditLogger> logger)
    {
        _db = db;
        _user = user;
        _http = http;
        _logger = logger;
    }

    public async Task LogAsync(
        string entityType, Guid? entityId, string action,
        object? before = null, object? after = null, CancellationToken ct = default)
    {
        var userId = _user.IsAuthenticated ? _user.UserId : (Guid?)null;
        // The username when someone is signed in; otherwise whatever synthetic
        // identity the background path bound. Null only when neither applies, which
        // is a code path that forgot to say who it was.
        var actor = _user.IsAuthenticated
            ? (string.IsNullOrWhiteSpace(_user.Username) ? null : _user.Username)
            : AuditActor.Current;
        var ctx = _http.HttpContext;
        var beforeJson = Serialize(before);
        var afterJson = Serialize(after);

        // Rounded down to what the timestamp column can hold, so the row in memory and the
        // row that comes back out of the database carry the same instant — and therefore
        // the same hash.
        var at = AuditChain.ToStorageResolution(DateTime.UtcNow);

        await Gate.WaitAsync(ct);
        // Declared outside the try so the catch can detach it. Null until it is built,
        // which is also how the catch knows whether anything reached the tracker.
        AuditEvent? row = null;
        try
        {
            var last = await _db.AuditEvents.AsNoTracking()
                .OrderByDescending(a => a.Sequence)
                .Select(a => new { a.Sequence, a.Hash })
                .FirstOrDefaultAsync(ct);

            var sequence = (last?.Sequence ?? 0) + 1;
            var prevHash = last?.Hash;
            var hash = AuditChain.ComputeHash(
                sequence, prevHash, userId, actor, entityType, entityId, action,
                beforeJson, afterJson, at);

            row = new AuditEvent
            {
                AuditEventId = Guid.NewGuid(),
                Sequence = sequence,
                UserId = userId,
                Actor = actor,
                HashVersion = AuditHashVersion.Current,
                EntityType = entityType,
                EntityId = entityId,
                Action = action,
                BeforeJson = beforeJson,
                AfterJson = afterJson,
                Ip = ClientIp.Resolve(ctx),
                UserAgent = ctx?.Request.Headers.UserAgent.ToString(),
                RequestId = ctx?.TraceIdentifier,
                At = at,
                PrevHash = prevHash,
                Hash = hash,
            };

            _db.AuditEvents.Add(row);
            await _db.SaveChangesAsync(ct);
        }
        // Cancellation is excluded deliberately: a client disconnecting mid-request, or
        // the host shutting down, is not a hole in the trail and must not fire the alarm
        // that says one exists.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed, matching AuditMutationFilter's policy — the two must agree,
            // because they are two halves of the same trail.
            //
            // Every caller writes this AFTER committing its mutation, so rethrowing
            // answered a request that had already taken effect with a 500: the client
            // retries, the change lands twice, and the trail is still missing the first
            // one. Losing the row is bad; lying about whether the change happened is
            // worse.
            //
            // Error level precisely so it can be alerted on — it means the trail has a
            // hole, and the chain detects a MODIFIED row, never a missing one.
            _logger.LogError(ex,
                "audit.write.failed entity_type={EntityType} entity_id={EntityId} action={Action} actor={Actor}",
                entityType, entityId, action, actor);

            // Detaching is the load-bearing part of swallowing at all.
            //
            // EF only accepts changes on success, so a failed row stays `Added` in a
            // change tracker shared by everything in the request scope. The next
            // LogAsync — WorkflowRunService writes one per node — would re-read the same
            // MAX(Sequence), pick the same number, and try to insert BOTH rows, colliding
            // on the unique index every time: one transient failure becomes the
            // guaranteed loss of every remaining row. Worse, the caller's own later
            // SaveChangesAsync would throw on the same stale insert, taking an unrelated
            // write down with it.
            if (row is not null) _db.Entry(row).State = EntityState.Detached;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value);
}
