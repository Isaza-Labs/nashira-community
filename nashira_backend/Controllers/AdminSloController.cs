using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Metrics;
using nashira_backend.Data.Models;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Slo;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Service-level objectives: what the platform commits to, measured, and the thresholds
// it is measured against.
//
// The measurement is delegated to SloComputeService, which the daily breach sweep also
// calls — so a green screen and a trail full of breach rows cannot describe the same
// window. Threshold edits are ordinary mutations and are picked up by the global audit
// filter; moving an objective is exactly the kind of change somebody will later want to
// find in the trail.
[ApiController]
[Route("api/admin/slo")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class AdminSloController : ControllerBase
{
    private const int DefaultDays = 7;

    private readonly AppDbContext _db;
    private readonly SloComputeService _compute;
    private readonly ICurrentUser _user;

    public AdminSloController(AppDbContext db, SloComputeService compute, ICurrentUser user)
    {
        _db = db;
        _compute = compute;
        _user = user;
    }

    [HttpGet]
    public async Task<ActionResult<SloSnapshotResponse>> Get(
        int days = DefaultDays, CancellationToken ct = default)
    {
        var snapshot = await _compute.ComputeAsync(days, ct);
        return new SloSnapshotResponse
        {
            Days = snapshot.Days,
            From = snapshot.From,
            Slos = snapshot.Slos.Select(e => new SloDto
            {
                Key = e.Key,
                Label = e.Label,
                Unit = e.Unit,
                Target = e.Target,
                DefaultTarget = e.DefaultTarget,
                Value = e.Value,
                Better = e.Better,
                Method = e.Method,
                // Decided here rather than in the client: the breach rule is the same
                // one the watcher alerts on, and two implementations of "is this bad"
                // is how a dashboard ends up disagreeing with its own alerts.
                Breach = SloComputeService.IsBreach(e),
                UpdatedBy = e.UpdatedBy,
            }).ToList(),
        };
    }

    // Move one objective's threshold.
    [HttpPut("targets/{key}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<IActionResult> SetTarget(
        string key, [FromBody] SloTargetRequest body, CancellationToken ct = default)
    {
        var definition = SloDefinition.Find(key);
        if (definition is null)
            return NotFound(new { error = $"unknown objective '{key}'" });

        // A ratio above 1 is not a strict target, it is an unreachable one. Rejected
        // rather than clamped: silently storing 1 when an admin typed 95 would leave
        // them looking at a number they did not enter.
        if (definition.Unit == "ratio" && body.Target > 1)
            return BadRequest(new
            {
                error = $"'{key}' is a ratio — its target must be between 0 and 1 "
                        + $"(0.95 for 95%), not {body.Target}",
            });

        var row = await _db.SloTargets.FirstOrDefaultAsync(t => t.Key == key, ct);
        var now = DateTime.UtcNow;
        var actor = _user.IsAuthenticated ? _user.Username : null;

        if (row is null)
        {
            row = new SloTarget
            {
                SloTargetId = Guid.NewGuid(),
                Key = key,
                Target = body.Target,
                UpdatedBy = actor,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.SloTargets.Add(row);
        }
        else
        {
            row.Target = body.Target;
            row.UpdatedBy = actor;
            row.UpdatedAt = now;
            // A previously reset objective is revived rather than duplicated — the
            // unique index on Key would reject a second row anyway.
            row.IsActive = true;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { key, target = row.Target, default_target = definition.DefaultTarget });
    }

    // Put an objective back on its built-in threshold.
    [HttpDelete("targets/{key}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<IActionResult> ResetTarget(string key, CancellationToken ct = default)
    {
        var definition = SloDefinition.Find(key);
        if (definition is null)
            return NotFound(new { error = $"unknown objective '{key}'" });

        var row = await _db.SloTargets.FirstOrDefaultAsync(t => t.Key == key, ct);
        // Already on the default. Reported as success: the caller asked for a state,
        // not for an action, and it holds.
        if (row is null) return Ok(new { key, target = definition.DefaultTarget, reset = false });

        // Hard delete rather than soft. The row's whole meaning is "this objective has
        // been moved"; an inactive one says that and denies it in the same breath, and
        // the unique index would then block the next edit.
        _db.SloTargets.Remove(row);
        await _db.SaveChangesAsync(ct);
        return Ok(new { key, target = definition.DefaultTarget, reset = true });
    }
}
