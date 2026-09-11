using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Traces;
using nashira_backend.Data.Models;
using nashira_backend.Services.Audit;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// The operational trail, read-only.
//
// Two things it is for, and they want different queries:
//   Live tail — "what is the platform doing right now", newest first, refreshed.
//   Forensics — "reconstruct this one request", by request id, oldest first, across
//               this table and the audit trail, which stores the same id.
//
// Nothing here mutates, so `[SkipAudit]` is unnecessary; and the middleware excludes
// this route, because a screen that polls itself would fill the table with the act of
// reading it.
[ApiController]
[Route("api/admin/traces")]
[Authorize(Policy = "Admin")]
[SkipAudit]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class TraceEventsController : ControllerBase
{
    private const int MaxLimit = 500;
    private const int DefaultLimit = 100;

    private readonly AppDbContext _db;

    public TraceEventsController(AppDbContext db) => _db = db;

    /// <param name="search">
    /// One free-text box over every text column a trace carries — action, category,
    /// error message, request id, actor and the metadata blob. Deliberately not a
    /// prefix match on the action: nobody arriving here knows the dotted name from the
    /// left, they know a fragment — "timeout", "claim", the job id they saw in a log.
    /// </param>
    /// <param name="user">
    /// Who fired it, by whatever the caller happens to have: the user id, part of a
    /// username, part of an email, or the actor string an unattended run bound itself
    /// to. Matching a name or mail needs a join to users; matching an actor does not,
    /// which is the only way to reach work that ran with no signed-in user behind it.
    /// </param>
    [HttpGet]
    public async Task<ActionResult<TraceListResponse>> List(
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        [FromQuery] string? status = null,
        [FromQuery] string? user = null,
        [FromQuery(Name = "request_id")] string? requestId = null,
        [FromQuery(Name = "min_duration_ms")] int? minDurationMs = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = DefaultLimit,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var q = _db.TraceEvents.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(t => t.Category == category);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(t => t.Status == status);
        // Kept alongside `search` for the deep links: request_id= means exactly this
        // request, where the same value typed into the search box would also drag in
        // every row that merely mentions it.
        if (!string.IsNullOrWhiteSpace(requestId)) q = q.Where(t => t.RequestId == requestId);
        if (from is { } f) q = q.Where(t => t.At >= f);
        if (to is { } t2) q = q.Where(t => t.At <= t2);

        // "Show me everything that took more than two seconds" is the query that finds
        // the problem when nothing has actually failed yet.
        if (minDurationMs is { } ms) q = q.Where(t => t.DurationMs != null && t.DurationMs >= ms);

        // Substring, case-insensitive, over every text column. `Contains` rather than
        // `EF.Functions.Like`: the provider parameterises and escapes the term itself,
        // so a term full of % and _ — which dotted action names and JSON metadata are —
        // stays literal instead of turning into a wildcard that matches everything
        // while looking like a filter.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            q = q.Where(t =>
                t.Action.ToLower().Contains(term)
                || t.Category.ToLower().Contains(term)
                || (t.ErrorMessage != null && t.ErrorMessage.ToLower().Contains(term))
                || (t.RequestId != null && t.RequestId.ToLower().Contains(term))
                || (t.Actor != null && t.Actor.ToLower().Contains(term))
                // Metadata is where the useful fragment usually is: the job id, the
                // model name, the device count. Untyped text, so it scans — acceptable
                // because every other filter on this screen narrows before it.
                || (t.MetadataJson != null && t.MetadataJson.ToLower().Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(user))
        {
            var who = user.Trim();
            if (Guid.TryParse(who, out var uid))
            {
                q = q.Where(t => t.UserId == uid);
            }
            else
            {
                var term = who.ToLower();
                var ids = _db.Users
                    .AsNoTracking()
                    .Where(u => u.Username.ToLower().Contains(term) || u.Email.ToLower().Contains(term))
                    .Select(u => u.UserId);

                q = q.Where(t =>
                    (t.UserId != null && ids.Contains(t.UserId.Value))
                    || (t.Actor != null && t.Actor.ToLower().Contains(term)));
            }
        }

        var total = await q.CountAsync(ct);

        var take = Math.Clamp(limit, 1, MaxLimit);

        // Asking for "slower than 2 s" and getting the newest 150 rows that qualify
        // answers a question nobody asked: the answer wanted is the slowest ones, and a
        // time-ordered page of a wide window will not contain them. So the filter picks
        // the order too, and the response says which order it used.
        var ordered = minDurationMs is not null
            ? q.OrderByDescending(t => t.DurationMs).ThenByDescending(t => t.At)
            : q.OrderByDescending(t => t.At).ThenByDescending(t => t.UpdatedAt);

        var rows = await ordered
            .Skip(Math.Max(0, offset))
            .Take(take)
            .Select(t => new TraceEventDto
            {
                TraceEventId = t.TraceEventId,
                Category = t.Category,
                Action = t.Action,
                Status = t.Status,
                DurationMs = t.DurationMs,
                Actor = t.Actor,
                UserId = t.UserId,
                RequestId = t.RequestId,
                ErrorMessage = t.ErrorMessage,
                MetadataJson = t.MetadataJson,
                At = t.At,
            })
            .ToListAsync(ct);

        return new TraceListResponse
        {
            Total = total,
            Limit = take,
            Offset = Math.Max(0, offset),
            SortedBy = minDurationMs is not null ? "duration" : "at",
            Items = rows,
        };
    }

    // Counts for the filter chips, over the same window the list is showing. Computed
    // server-side because the client only ever holds one page and would otherwise
    // report "3 failed" when it means "3 failed on this page".
    [HttpGet("summary")]
    public async Task<ActionResult<TraceSummaryResponse>> Summary(
        [FromQuery] int minutes = 60, CancellationToken ct = default)
    {
        var window = Math.Clamp(minutes, 1, 60 * 24 * 7);
        var from = DateTime.UtcNow.AddMinutes(-window);
        var q = _db.TraceEvents.AsNoTracking().Where(t => t.At >= from);

        var byCategory = await q
            .GroupBy(t => t.Category)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var byStatus = await q
            .GroupBy(t => t.Status)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // The slowest thing that finished. What the screen is opened to find, and the
        // one number a list sorted by time will never show you.
        var slowest = await q
            .Where(t => t.DurationMs != null)
            .OrderByDescending(t => t.DurationMs)
            .Select(t => new TraceEventDto
            {
                TraceEventId = t.TraceEventId,
                Category = t.Category,
                Action = t.Action,
                Status = t.Status,
                DurationMs = t.DurationMs,
                Actor = t.Actor,
                RequestId = t.RequestId,
                At = t.At,
            })
            .Take(5)
            .ToListAsync(ct);

        return new TraceSummaryResponse
        {
            Minutes = window,
            From = from,
            Total = byCategory.Values.Sum(),
            ByCategory = byCategory,
            ByStatus = byStatus,
            // Started and never closed: work that is stuck, which is invisible everywhere
            // else because a row that was never written cannot be queried.
            InFlight = byStatus.GetValueOrDefault(TraceEvent.StatusStarted),
            Failed = byStatus.GetValueOrDefault(TraceEvent.StatusFailed),
            Slowest = slowest,
        };
    }
}
