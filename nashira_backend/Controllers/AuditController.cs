using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Audit;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using AuditEntity = nashira_backend.Data.Models.AuditEvent;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Audit trail. Admin-only. The trail itself is append-only (no create/update/delete
// surface) and hash-chained; /verify recomputes the chain to prove it is intact. The
// one write here — /{id}/restore — mutates the ENTITY a delete event points at, never
// a trail row, and appends its own "restore" event like any other mutation.
[ApiController]
[Route("api/audit")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class AuditController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly EntityRestoreService _restore;
    private readonly IAuditLogger _audit;

    public AuditController(AppDbContext db, ICurrentUser user, EntityRestoreService restore, IAuditLogger audit)
    {
        _db = db;
        _user = user;
        _restore = restore;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<AuditEventSummary>>> Get(
        string? entityType = null, string? entityTypes = null, Guid? entityId = null,
        string? action = null, string? actionPrefix = null, Guid? userId = null, string? actor = null,
        string? requestId = null, DateTime? from = null, DateTime? to = null,
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(a => a.EntityType == entityType);

        // A comma-separated set, because the categories worth having span several entity
        // types at once: "security" is credentials AND secrets AND users AND permissions.
        // With one type per query the screen would need four chips for one question, and
        // the answer would arrive in four separate lists.
        if (!string.IsNullOrWhiteSpace(entityTypes))
        {
            var wanted = entityTypes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (wanted.Count > 0) q = q.Where(a => wanted.Contains(a.EntityType));
        }
        // "Show me the history of this one credential / workflow / user." EntityId was
        // stored from the first row and had no filter, so the single most natural audit
        // question had no answer through the API.
        if (entityId is { } eid) q = q.Where(a => a.EntityId == eid);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
        // A category spans several verbs (credential.create / .update / .delete), so the
        // UI's quick filters need a prefix rather than an exact-match matrix.
        if (!string.IsNullOrWhiteSpace(actionPrefix))
        {
            // Escaped: `_` and `%` are LIKE wildcards, and the actions here are full of
            // underscores. Unescaped, `retry_install` also matches `retryXinstall`, and
            // a lone `%` matches everything while looking like a filter.
            var prefix = actionPrefix
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
            q = q.Where(a => EF.Functions.Like(a.Action, prefix + "%", "\\"));
        }
        // "Who did this, and when" is the question an audit trail is opened with; without
        // these two the answer is scrolling.
        if (userId is { } uid) q = q.Where(a => a.UserId == uid);
        // Automation has no UserId at all, so actor is the only way to ask what the
        // scheduled runs changed last night.
        if (!string.IsNullOrWhiteSpace(actor)) q = q.Where(a => a.Actor == actor);
        // The join to the operational trail: trace_events stores the same value, so one
        // request can be read end to end across both — what it did, and what it changed.
        // Stored since the first row and, until now, filterable in neither.
        if (!string.IsNullOrWhiteSpace(requestId)) q = q.Where(a => a.RequestId == requestId);
        if (from is { } f) q = q.Where(a => a.At >= f.ToUniversalTime());
        if (to is { } t) q = q.Where(a => a.At <= t.ToUniversalTime());

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(a => a.Sequence).Skip(offset).Take(limit).ToListAsync(ct);
        var usernames = await UsernamesAsync(rows.Select(r => r.UserId), ct);

        return new OkObjectResult(new ListResponse<AuditEventSummary>
        {
            Items = rows.Select(r => WithUsername(ToSummary(r), usernames)).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    private async Task<Dictionary<Guid, string>> UsernamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await _db.Users.AsNoTracking()
            .Where(u => wanted.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, u => u.Username, ct);
    }

    private static T WithUsername<T>(T summary, Dictionary<Guid, string> usernames) where T : AuditEventSummary
    {
        if (summary.UserId is { } id) summary.Username = usernames.GetValueOrDefault(id);
        return summary;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AuditEventDetail>> GetById(Guid id, CancellationToken ct)
    {
        var e = await _db.AuditEvents.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AuditEventId == id, ct);
        if (e is null) throw new NotFoundException("audit event not found");

        var usernames = await UsernamesAsync([e.UserId], ct);

        return WithUsername(new AuditEventDetail
        {
            AuditEventId = e.AuditEventId,
            Sequence = e.Sequence,
            At = e.At,
            UserId = e.UserId,
            EntityType = e.EntityType,
            EntityId = e.EntityId,
            Action = e.Action,
            Actor = e.Actor,
            Ip = e.Ip,
            RequestId = e.RequestId,
            Hash = e.Hash,
            HashVersion = e.HashVersion,
            PrevHash = e.PrevHash,
            UserAgent = e.UserAgent,
            Before = Parse(e.BeforeJson),
            After = Parse(e.AfterJson),
            Restorable = e.Action == "delete" && e.EntityId is not null
                && EntityRestoreService.CanRestore(e.EntityType),
        }, usernames);
    }

    // Brings back the soft-deleted record a delete event points at. [SkipAudit]
    // because the generic filter would record this as a mutation of "audit" — the
    // row that matters is written explicitly below, with the real entity type, a
    // "restore" action, and the signed-in admin as its actor.
    [HttpPost("{id:guid}/restore")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [SkipAudit]
    public async Task<ActionResult<AuditRestoreResponse>> Restore(Guid id, CancellationToken ct)
    {
        var e = await _db.AuditEvents.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AuditEventId == id, ct);
        if (e is null) throw new NotFoundException("audit event not found");
        if (e.Action != "delete")
            throw new ValidationException("only delete events can be restored");
        if (e.EntityId is not { } entityId)
            throw new ValidationException("this event does not reference a restorable record");

        var result = await _restore.RestoreAsync(e.EntityType, entityId, ct);

        await _audit.LogAsync(e.EntityType, entityId, "restore",
            before: new { is_active = false },
            after: new { is_active = true, name = result.Name, restored_from_sequence = e.Sequence },
            ct);

        return new OkObjectResult(new AuditRestoreResponse
        {
            EntityType = result.EntityType,
            EntityId = result.EntityId,
            Name = result.Name,
        });
    }

    [HttpGet("verify")]
    public async Task<ActionResult<AuditVerifyResponse>> Verify(CancellationToken ct)
    {
        var ordered = await _db.AuditEvents.AsNoTracking()
            
            .OrderBy(a => a.Sequence)
            .ToListAsync(ct);
        var result = AuditChain.Verify(ordered);
        return new AuditVerifyResponse
        {
            Valid = result.Valid,
            Count = result.Count,
            BrokenAtSequence = result.BrokenAtSequence,
            Reason = result.Reason,
        };
    }

    private static AuditEventSummary ToSummary(AuditEntity a) => new()
    {
        AuditEventId = a.AuditEventId,
        Sequence = a.Sequence,
        At = a.At,
        UserId = a.UserId,
        EntityType = a.EntityType,
        EntityId = a.EntityId,
        Action = a.Action,
        Actor = a.Actor,
        Ip = a.Ip,
        RequestId = a.RequestId,
        Hash = a.Hash,
        HashVersion = a.HashVersion,
    };

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
