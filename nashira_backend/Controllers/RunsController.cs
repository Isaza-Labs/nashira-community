using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Fleet;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Common;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Runs and schedules across every workflow.
//
// Everything about a run already existed, but only underneath the workflow that owns
// it: `/api/workflows/{id}/runs`. That answers "what has this one done" and cannot
// answer "what happened last night", which is the question an operator actually starts
// the day with — and the one the dashboard's top-failing list points at without being
// able to open.
//
// Filtering is server-side, deliberately. Narrowing the current page in the browser is
// cheaper to write and produces a list that says "3 results" beside a pager built from
// 412, so the count and the rows disagree and the reader believes the smaller one.
[ApiController]
[Route("api")]
[Authorize]
[SkipAudit]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class RunsController : ControllerBase
{
    private readonly AppDbContext _db;

    public RunsController(AppDbContext db) => _db = db;

    [HttpGet("runs")]
    public async Task<ActionResult<ListResponse<FleetRunResponse>>> Runs(
        string? status = null,
        string? environment = null,
        [FromQuery(Name = "final_state")] string? finalState = null,
        [FromQuery(Name = "workflow_id")] Guid? workflowId = null,
        string? q = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        // Joined rather than looked up afterwards: a run whose workflow was deleted
        // still happened, and a left join keeps it in the list under "(deleted)"
        // instead of dropping it from the record.
        var query =
            from r in _db.WorkflowRuns.AsNoTracking()
            join w in _db.Workflows.AsNoTracking() on r.WorkflowId equals w.WorkflowId into ws
            from w in ws.DefaultIfEmpty()
            where r.IsActive
            select new { Run = r, WorkflowName = w != null ? w.Name : null };

        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Run.Status == status);
        if (!string.IsNullOrWhiteSpace(environment)) query = query.Where(x => x.Run.Environment == environment);
        if (!string.IsNullOrWhiteSpace(finalState)) query = query.Where(x => x.Run.FinalState == finalState);
        if (workflowId is { } wid) query = query.Where(x => x.Run.WorkflowId == wid);
        if (from is { } f) query = query.Where(x => x.Run.StartedAt >= f.ToUniversalTime());
        if (to is { } t) query = query.Where(x => x.Run.StartedAt <= t.ToUniversalTime());

        // Contains, lower-cased on both sides. There is an index on Workflow.Name, but a
        // substring search cannot use it however it is written — a leading wildcard is a
        // scan either way — so nothing is given up by choosing the form that also
        // translates outside Postgres and can therefore be tested. It parameterises the
        // term as well, which is a stronger guarantee than escaping % and _ by hand.
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLowerInvariant();
            query = query.Where(x => x.WorkflowName != null
                && x.WorkflowName.ToLower().Contains(needle));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.Run.StartedAt)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);

        return new ListResponse<FleetRunResponse>
        {
            Items = rows.Select(x => new FleetRunResponse
            {
                WorkflowRunId = x.Run.WorkflowRunId,
                WorkflowId = x.Run.WorkflowId,
                WorkflowName = x.WorkflowName ?? "(deleted)",
                Environment = x.Run.Environment,
                Status = x.Run.Status,
                FinalState = x.Run.FinalState,
                NodeCount = x.Run.NodeCount,
                ChangedCount = x.Run.ChangedCount,
                FailedCount = x.Run.FailedCount,
                StartedAt = x.Run.StartedAt,
                FinishedAt = x.Run.FinishedAt,
                DurationSeconds = x.Run.FinishedAt is { } fin
                    ? (int)Math.Max(0, (fin - x.Run.StartedAt).TotalSeconds)
                    : null,
                Trigger = x.Run.Trigger,
                ParentRunId = x.Run.ParentRunId,
                Error = x.Run.Error,
            }).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        };
    }

    // One run, with its steps but without their payloads.
    //
    // /api/workflows/runs/{id} answers the same question and inlines every step's
    // output, input and logs. That is the right shape for the card that shows a run
    // the moment after it was started, and the wrong one for a page opened from a
    // list: a per-device node stores a payload per device, so the first paint of a
    // twelve-device run waited on megabytes nobody had asked to see yet. The payloads
    // are fetched per step, on expand, from the endpoint below — and their sizes
    // travel here so the screen can say what a click will cost before it is made.
    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<FleetRunDetailResponse>> RunDetail(Guid runId, CancellationToken ct)
    {
        var row = await (
            from r in _db.WorkflowRuns.AsNoTracking()
            join w in _db.Workflows.AsNoTracking() on r.WorkflowId equals w.WorkflowId into ws
            from w in ws.DefaultIfEmpty()
            where r.WorkflowRunId == runId
            select new { Run = r, WorkflowName = w != null ? w.Name : null })
            .FirstOrDefaultAsync(ct);
        if (row is null) throw new NotFoundException("workflow run not found");

        // Projected, not materialised: the text columns never leave the database.
        // `Length` translates to length() in Postgres, so the size costs one function
        // call per row instead of the transfer it describes.
        var steps = await _db.StepRuns.AsNoTracking()
            .Where(s => s.WorkflowRunId == runId)
            .OrderBy(s => s.Sequence)
            .Select(s => new FleetStepResponse
            {
                NodeId = s.NodeId,
                Sequence = s.Sequence,
                Result = s.Result,
                ErrorCode = s.ErrorCode,
                Retryable = s.Retryable,
                Error = s.Error,
                Attempts = s.Attempts,
                StartedAt = s.StartedAt,
                FinishedAt = s.FinishedAt,
                OutputChars = s.OutputJson == null ? 0 : s.OutputJson.Length,
                InputChars = s.InputJson == null ? 0 : s.InputJson.Length,
                LogsChars = s.Logs == null ? 0 : s.Logs.Length,
                ChildRunId = s.ChildRunId,
            })
            .ToListAsync(ct);

        foreach (var s in steps)
        {
            s.DurationMs = s.StartedAt is { } from && s.FinishedAt is { } to && to >= from
                ? (int)(to - from).TotalMilliseconds
                : null;
        }

        var run = row.Run;
        return new FleetRunDetailResponse
        {
            WorkflowRunId = run.WorkflowRunId,
            WorkflowId = run.WorkflowId,
            WorkflowName = row.WorkflowName ?? "(deleted)",
            Environment = run.Environment,
            Status = run.Status,
            FinalState = run.FinalState,
            NodeCount = run.NodeCount,
            ChangedCount = run.ChangedCount,
            FailedCount = run.FailedCount,
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            DurationSeconds = run.FinishedAt is { } fin
                ? (int)Math.Max(0, (fin - run.StartedAt).TotalSeconds)
                : null,
            Trigger = run.Trigger,
            ParentRunId = run.ParentRunId,
            Error = run.Error,
            Input = ParseJson(run.InputJson),
            TargetDevices = ParseJson(string.IsNullOrWhiteSpace(run.TargetDevicesJson) ? "[]" : run.TargetDevicesJson)
                ?? JsonDocument.Parse("[]").RootElement.Clone(),
            Steps = steps,
        };
    }

    // One step's payloads. Only the row asked for is read, and only its text columns.
    [HttpGet("runs/{runId:guid}/steps/{sequence:int}")]
    public async Task<ActionResult<FleetStepPayloadResponse>> StepPayload(
        Guid runId, int sequence, CancellationToken ct)
    {
        var step = await _db.StepRuns.AsNoTracking()
            .Where(s => s.WorkflowRunId == runId && s.Sequence == sequence)
            .Select(s => new { s.NodeId, s.Sequence, s.OutputJson, s.InputJson, s.Logs })
            .FirstOrDefaultAsync(ct);
        if (step is null) throw new NotFoundException("step not found");

        return new FleetStepPayloadResponse
        {
            NodeId = step.NodeId,
            Sequence = step.Sequence,
            Output = ParseJson(step.OutputJson),
            Input = ParseJson(step.InputJson),
            Logs = step.Logs,
        };
    }

    // Stored payloads are opaque text written by the engine. Returned as JSON when they
    // parse and dropped when they do not — one malformed fragment must not make the
    // rest of the run unreadable.
    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
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

    // Every trigger on the platform, cron and webhook alike.
    //
    // Flow-weaver assembles this in the browser: list the workflows, then fetch the
    // triggers of each and merge. That is one request per workflow on a page whose
    // whole purpose is to be opened when you do not know which workflow is involved.
    [HttpGet("schedules")]
    public async Task<ActionResult<ListResponse<FleetTriggerResponse>>> Schedules(
        string? type = null,
        bool? enabled = null,
        string? q = null,
        [FromQuery(Name = "due_within_hours")] int? dueWithinHours = null,
        int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var query =
            from t in _db.WorkflowTriggers.AsNoTracking()
            join w in _db.Workflows.AsNoTracking() on t.WorkflowId equals w.WorkflowId into ws
            from w in ws.DefaultIfEmpty()
            where t.IsActive
            select new { Trigger = t, Workflow = w };

        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.Trigger.Type == type);
        if (enabled is { } en) query = query.Where(x => x.Trigger.Enabled == en);

        // "What is about to run" — the question that makes this page worth opening
        // before something breaks rather than after.
        if (dueWithinHours is { } hours)
        {
            var until = DateTime.UtcNow.AddHours(Math.Clamp(hours, 1, 24 * 30));
            query = query.Where(x => x.Trigger.NextRunAt != null && x.Trigger.NextRunAt <= until);
        }

        // The trigger's own name or the workflow it belongs to: somebody looking for
        // "the nightly one" may remember either.
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLowerInvariant();
            query = query.Where(x =>
                x.Trigger.Name.ToLower().Contains(needle)
                || (x.Workflow != null && x.Workflow.Name.ToLower().Contains(needle)));
        }

        var total = await query.CountAsync(ct);

        // Soonest first, and the ones with no next firing last rather than first: a
        // disabled or unschedulable trigger sorts as null, and nulls-first would open
        // the page on everything that is never going to run.
        var rows = await query
            .OrderBy(x => x.Trigger.NextRunAt == null)
            .ThenBy(x => x.Trigger.NextRunAt)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        return new ListResponse<FleetTriggerResponse>
        {
            Items = rows.Select(x => new FleetTriggerResponse
            {
                WorkflowTriggerId = x.Trigger.WorkflowTriggerId,
                WorkflowId = x.Trigger.WorkflowId,
                WorkflowName = x.Workflow?.Name ?? "(deleted)",
                WorkflowEnvironment = x.Workflow?.Environment ?? string.Empty,
                Name = x.Trigger.Name,
                Type = x.Trigger.Type,
                Enabled = x.Trigger.Enabled,
                CronExpression = x.Trigger.CronExpression,
                Timezone = x.Trigger.Timezone,
                Route = x.Trigger.Route,
                NextRunAt = x.Trigger.NextRunAt,
                LastRunAt = x.Trigger.LastRunAt,
                LastRunStatus = x.Trigger.LastRunStatus,
                LastRunId = x.Trigger.LastRunId,
                LastError = x.Trigger.LastError,
                FireCount = x.Trigger.FireCount,
                // A cron whose next firing is in the past means the scheduler has not
                // been round to it. Computed here because a client comparing to its own
                // clock would report every trigger as overdue whenever a laptop's time
                // drifted.
                Overdue = x.Trigger.Enabled
                          && x.Trigger.NextRunAt != null
                          && x.Trigger.NextRunAt < now.AddMinutes(-5),
            }).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        };
    }

}
