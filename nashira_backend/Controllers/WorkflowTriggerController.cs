using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Scheduler;
using nashira_backend.Services.Security;
using TriggerEntity = nashira_backend.Data.Models.WorkflowTrigger;
using Microsoft.AspNetCore.RateLimiting;

namespace nashira_backend.Controllers;

public class WriteWorkflowTrigger
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("cron_expression")] public string? CronExpression { get; set; }
    [JsonPropertyName("timezone")] public string? Timezone { get; set; }
    [JsonPropertyName("target_devices")] public List<Guid>? TargetDevices { get; set; }
    [JsonPropertyName("input_defaults")] public JsonElement? InputDefaults { get; set; }
    [JsonPropertyName("allow_unsigned")] public bool? AllowUnsigned { get; set; }
    [JsonPropertyName("allow_target_override")] public bool? AllowTargetOverride { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

public class WorkflowTriggerResponse
{
    [JsonPropertyName("workflow_trigger_id")] public Guid Id { get; set; }
    [JsonPropertyName("workflow_id")] public Guid WorkflowId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("cron_expression")] public string? CronExpression { get; set; }
    [JsonPropertyName("timezone")] public string Timezone { get; set; } = "UTC";
    [JsonPropertyName("route")] public string? Route { get; set; }
    [JsonPropertyName("has_secret")] public bool HasSecret { get; set; }
    [JsonPropertyName("allow_unsigned")] public bool AllowUnsigned { get; set; }
    [JsonPropertyName("allow_target_override")] public bool AllowTargetOverride { get; set; }
    [JsonPropertyName("target_devices")] public JsonElement TargetDevices { get; set; }
    [JsonPropertyName("input_defaults")] public JsonElement? InputDefaults { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("next_run_at")] public DateTime? NextRunAt { get; set; }
    [JsonPropertyName("last_run_at")] public DateTime? LastRunAt { get; set; }
    [JsonPropertyName("last_run_status")] public string? LastRunStatus { get; set; }
    [JsonPropertyName("last_run_id")] public Guid? LastRunId { get; set; }
    [JsonPropertyName("last_error")] public string? LastError { get; set; }
    [JsonPropertyName("fire_count")] public int FireCount { get; set; }
    // Present exactly once, on create and on rotate. There is no endpoint that
    // returns it afterwards.
    [JsonPropertyName("secret")] public string? Secret { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

// Cron and webhook triggers for a workflow. Operator: a trigger decides when a
// workflow runs unattended, which is authoring, not administration.
[ApiController]
[Route("api/workflows/{workflowId:guid}/triggers")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class WorkflowTriggerController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISecretProtector _protector;

    public WorkflowTriggerController(AppDbContext db, ICurrentUser user, ISecretProtector protector)
    {
        _db = db;
        _user = user;
        _protector = protector;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<WorkflowTriggerResponse>>> Get(
        Guid workflowId, CancellationToken ct)
    {
        await EnsureWorkflow(workflowId, ct);
        var rows = await _db.WorkflowTriggers.AsNoTracking()
            .Where(t => t.WorkflowId == workflowId && t.IsActive)
            .OrderBy(t => t.Name).ToListAsync(ct);

        return new OkObjectResult(new ListResponse<WorkflowTriggerResponse>
        {
            Items = rows.Select(r => ToResponse(r)).ToList(),
            Total = rows.Count, Limit = rows.Count, Offset = 0,
        });
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<WorkflowTriggerResponse>> Post(
        Guid workflowId, [FromBody] WriteWorkflowTrigger dto, CancellationToken ct)
    {
        await EnsureWorkflow(workflowId, ct);
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");

        var type = (dto.Type ?? TriggerEntity.TypeCron).Trim().ToLowerInvariant();
        if (!TriggerEntity.Types.Contains(type))
            throw new ValidationException($"type must be one of: {string.Join(", ", TriggerEntity.Types)}");

        await EnsureDevicesAsync(dto.TargetDevices, ct);
        RequireObject(dto.InputDefaults, "input_defaults");

        var now = DateTime.UtcNow;
        var row = new TriggerEntity
        {
            WorkflowTriggerId = Guid.NewGuid(),
            WorkflowId = workflowId,
            Name = dto.Name.Trim(),
            Type = type,
            Description = dto.Description,
            TargetDevicesJson = JsonSerializer.Serialize(dto.TargetDevices ?? []),
            InputDefaultsJson = dto.InputDefaults?.GetRawText(),
            AllowUnsigned = dto.AllowUnsigned ?? false,
            AllowTargetOverride = dto.AllowTargetOverride ?? false,
            Enabled = dto.Enabled ?? true,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        string? plaintextSecret = null;

        if (type == TriggerEntity.TypeCron)
        {
            ValidateCron(dto.CronExpression, dto.Timezone);
            row.CronExpression = dto.CronExpression!.Trim();
            row.Timezone = string.IsNullOrWhiteSpace(dto.Timezone) ? "UTC" : dto.Timezone.Trim();
            row.NextRunAt = CronSchedule.NextOccurrence(row.CronExpression, row.Timezone, now);
            if (row.NextRunAt is null)
                throw new ValidationException("that cron expression has no future occurrence");
        }
        else
        {
            // Random route and secret, never derived from the name — see
            // WorkflowTriggerSecrets, shared with the bundle importer.
            row.Route = WorkflowTriggerSecrets.NewRoute();
            plaintextSecret = WorkflowTriggerSecrets.NewSecret();
            row.EncryptedSecret = _protector.Encrypt(plaintextSecret);
        }

        _db.WorkflowTriggers.Add(row);
        await _db.SaveChangesAsync(ct);
        return ToResponse(row, plaintextSecret);
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<WorkflowTriggerResponse>> Update(
        Guid workflowId, Guid id, [FromBody] WriteWorkflowTrigger dto, CancellationToken ct)
    {
        var row = await Find(workflowId, id, ct);
        var now = DateTime.UtcNow;

        if (dto.Name is not null && dto.Name.Trim().Length > 0) row.Name = dto.Name.Trim();
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.TargetDevices is not null)
        {
            await EnsureDevicesAsync(dto.TargetDevices, ct);
            row.TargetDevicesJson = JsonSerializer.Serialize(dto.TargetDevices);
        }
        if (dto.InputDefaults is not null)
        {
            RequireObject(dto.InputDefaults, "input_defaults");
            row.InputDefaultsJson = dto.InputDefaults.Value.GetRawText();
        }
        if (dto.AllowUnsigned is not null) row.AllowUnsigned = dto.AllowUnsigned.Value;
        if (dto.AllowTargetOverride is not null) row.AllowTargetOverride = dto.AllowTargetOverride.Value;
        if (dto.Enabled is not null) row.Enabled = dto.Enabled.Value;

        // Type is not editable: a cron trigger has no route or secret and a webhook
        // has no schedule, so switching would leave the row half-configured.
        if (row.Type == TriggerEntity.TypeCron)
        {
            // Only a change to the expression or the zone re-bases the firing. The
            // console serialises the whole form on every save, so keying this off "the
            // request mentioned a schedule" rescheduled the trigger every time anyone
            // renamed it — and an edit in the seconds between a firing coming due and
            // the sweep noticing dropped that run outright.
            var schedule = TriggerScheduleRules.Resolve(
                row.CronExpression, row.Timezone, dto.CronExpression, dto.Timezone);

            if (schedule.Changed)
            {
                ValidateCron(schedule.Expression, schedule.Timezone);
                row.CronExpression = schedule.Expression;
                row.Timezone = schedule.Timezone;
                row.NextRunAt = CronSchedule.NextOccurrence(row.CronExpression, row.Timezone, now);
                if (row.NextRunAt is null)
                    throw new ValidationException("that cron expression has no future occurrence");
                // The schedule that produced the old failure no longer exists, so the
                // row must stop reporting it — otherwise a fixed trigger shows a stale
                // error until it happens to run.
                row.LastError = null;
            }

            // Re-enabling recomputes a schedule that went by while the trigger was off,
            // so it fires next occurrence rather than immediately for a time that has
            // passed. NextRunAt is null here when the trigger has never been scheduled;
            // the scheduler's backfill owns that case.
            if (dto.Enabled == true && row.NextRunAt is { } pending && pending <= now)
            {
                row.NextRunAt = CronSchedule.NextOccurrence(row.CronExpression ?? string.Empty, row.Timezone, now);
                row.LastError = null;
            }
        }

        row.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<WorkflowTriggerResponse>> Delete(
        Guid workflowId, Guid id, CancellationToken ct)
    {
        var row = await Find(workflowId, id, ct);
        row.IsActive = false;
        row.Enabled = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    // Issues a new secret and returns it once. The old one stops working
    // immediately — a rotation that kept both valid would not be a rotation.
    [HttpPost("{id:guid}/rotate-secret")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<WorkflowTriggerResponse>> RotateSecret(
        Guid workflowId, Guid id, CancellationToken ct)
    {
        var row = await Find(workflowId, id, ct);
        if (row.Type != TriggerEntity.TypeWebhook)
            throw new ValidationException("only webhook triggers have a secret");

        var secret = WorkflowTriggerSecrets.NewSecret();
        row.EncryptedSecret = _protector.Encrypt(secret);
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row, secret);
    }

    private static void ValidateCron(string? expression, string? timezone)
    {
        if (!CronSchedule.TryParse(expression, out _, out var cronError))
            throw new ValidationException(cronError!);
        if (!CronSchedule.IsValidTimezone(timezone, out _, out var tzError))
            throw new ValidationException(tzError!);
    }

    private static void RequireObject(JsonElement? value, string field)
    {
        if (value is null) return;
        if (value.Value.ValueKind != JsonValueKind.Object)
            throw new ValidationException($"{field} must be a JSON object");
    }

    private async Task EnsureDevicesAsync(List<Guid>? ids, CancellationToken ct)
    {
        if (ids is null || ids.Count == 0) return;
        var known = await _db.Devices.AsNoTracking()
            .Where(d => d.IsActive && ids.Contains(d.DeviceId))
            .Select(d => d.DeviceId).ToListAsync(ct);
        var missing = ids.Distinct().Except(known).ToList();
        if (missing.Count > 0)
            throw new ValidationException($"unknown device(s): {string.Join(", ", missing)}");
    }

    private async Task EnsureWorkflow(Guid id, CancellationToken ct)
    {
        if (!await _db.Workflows.AnyAsync(w => w.WorkflowId == id && w.IsActive, ct))
            throw new NotFoundException("workflow not found");
    }

    private async Task<TriggerEntity> Find(Guid workflowId, Guid id, CancellationToken ct)
    {
        var row = await _db.WorkflowTriggers.FirstOrDefaultAsync(
            t => t.WorkflowTriggerId == id && t.WorkflowId == workflowId && t.IsActive, ct);
        if (row is null) throw new NotFoundException("workflow trigger not found");
        return row;
    }

    private static WorkflowTriggerResponse ToResponse(TriggerEntity t, string? secret = null) => new()
    {
        Id = t.WorkflowTriggerId,
        WorkflowId = t.WorkflowId,
        Name = t.Name,
        Type = t.Type,
        Description = t.Description,
        CronExpression = t.CronExpression,
        Timezone = t.Timezone,
        Route = t.Route,
        HasSecret = t.EncryptedSecret is { Length: > 0 },
        AllowUnsigned = t.AllowUnsigned,
        AllowTargetOverride = t.AllowTargetOverride,
        TargetDevices = Parse(t.TargetDevicesJson, "[]"),
        InputDefaults = t.InputDefaultsJson is null ? null : Parse(t.InputDefaultsJson, "{}"),
        Enabled = t.Enabled,
        NextRunAt = t.NextRunAt,
        LastRunAt = t.LastRunAt,
        LastRunStatus = t.LastRunStatus,
        LastRunId = t.LastRunId,
        LastError = t.LastError,
        FireCount = t.FireCount,
        Secret = secret,
        UpdatedAt = t.UpdatedAt,
    };

    private static JsonElement Parse(string? json, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? fallback : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(fallback).RootElement.Clone();
        }
    }
}
