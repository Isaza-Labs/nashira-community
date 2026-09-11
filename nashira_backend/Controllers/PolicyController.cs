using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Policy;
using PolicyEntity = nashira_backend.Data.Models.Policy;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

public class WritePolicy
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("rule")] public JsonElement? Rule { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

public class PolicyResponse
{
    [JsonPropertyName("policy_id")] public Guid Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("rule")] public JsonElement Rule { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class EvaluatePolicyRequest
{
    // Omit to evaluate the stored, enabled policies. Provide one to dry-run a rule
    // that is not saved yet.
    [JsonPropertyName("rule")] public JsonElement? Rule { get; set; }
    [JsonPropertyName("environment")] public string? Environment { get; set; }
    [JsonPropertyName("workflow_description")] public string? WorkflowDescription { get; set; }
    [JsonPropertyName("device_roles")] public List<string>? DeviceRoles { get; set; }
    [JsonPropertyName("device_pools")] public List<string>? DevicePools { get; set; }
    [JsonPropertyName("snippet_types")] public List<string>? SnippetTypes { get; set; }
}

public class PolicyDecisionResponse
{
    [JsonPropertyName("denied")] public bool Denied { get; set; }
    [JsonPropertyName("policy")] public string? Policy { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

// Corporate guardrails. Admin-only: a policy can stop every run in the system.
[ApiController]
[Route("api/policies")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class PolicyController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IPolicyEvaluator _evaluator;
    private readonly IAuditLogger _audit;

    public PolicyController(
        AppDbContext db, ICurrentUser user, IPolicyEvaluator evaluator, IAuditLogger audit)
    {
        _db = db;
        _user = user;
        _evaluator = evaluator;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<PolicyResponse>>> Get(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.Policies.AsNoTracking().Where(p => p.IsActive);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(p => p.Name).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<PolicyResponse>
        {
            Items = rows.Select(ToResponse).ToList(), Total = total, Limit = limit, Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PolicyResponse>> GetById(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<PolicyResponse>> Post([FromBody] WritePolicy dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        var rule = RequireRule(dto.Rule);

        var name = dto.Name.Trim();
        if (await _db.Policies.AnyAsync(p => p.Name == name && p.IsActive, ct))
            throw new ConflictException("a policy with this name already exists", "policy_name_taken");

        var now = DateTime.UtcNow;
        var row = new PolicyEntity
        {
            PolicyId = Guid.NewGuid(),
            Name = name,
            Description = dto.Description,
            RuleJson = rule,
            Enabled = dto.Enabled ?? true,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Policies.Add(row);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("policy", row.PolicyId, "create", after: Snapshot(row), ct: ct);

        return new CreatedAtActionResult(nameof(GetById), "Policy", new { id = row.PolicyId }, ToResponse(row));
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<PolicyResponse>> Update(
        Guid id, [FromBody] WritePolicy dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var before = Snapshot(row);

        if (dto.Name is not null && dto.Name.Trim().Length > 0)
        {
            var name = dto.Name.Trim();
            if (await _db.Policies.AnyAsync(p => p.Name == name && p.IsActive && p.PolicyId != id, ct))
                throw new ConflictException("a policy with this name already exists", "policy_name_taken");
            row.Name = name;
        }
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.Rule is not null) row.RuleJson = RequireRule(dto.Rule);
        if (dto.Enabled is not null) row.Enabled = dto.Enabled.Value;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // The rule travels verbatim. A guardrail that stopped a run last week and
        // permits it today is only explicable if the trail shows what it used to say.
        await _audit.LogAsync("policy", row.PolicyId, "update",
            before: before, after: Snapshot(row), ct: ct);

        return ToResponse(row);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<PolicyResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var before = Snapshot(row);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Removing a guardrail is the change most worth being able to point at later.
        await _audit.LogAsync("policy", row.PolicyId, "delete", before: before, ct: ct);

        return ToResponse(row);
    }

    private static object Snapshot(PolicyEntity p) => new
    {
        name = p.Name,
        description = p.Description,
        rule = p.RuleJson,
        enabled = p.Enabled,
    };

    // Dry-run. A guardrail first exercised in production is a guardrail nobody has
    // read carefully, so an admin must be able to ask "would this rule stop that
    // run?" before enabling it.
    [HttpPost("evaluate")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    [SkipAudit] // read-only diagnostic
    public async Task<ActionResult<PolicyDecisionResponse>> Evaluate(
        [FromBody] EvaluatePolicyRequest req, CancellationToken ct)
    {
        var context = new PolicyContext(
            Environment: req.Environment ?? string.Empty,
            WorkflowDescription: req.WorkflowDescription,
            DeviceRoles: req.DeviceRoles,
            DevicePools: req.DevicePools,
            SnippetTypes: req.SnippetTypes);

        var decision = req.Rule is { } rule
            ? _evaluator.Test(rule.GetRawText(), context)
            : await _evaluator.EvaluateAsync(context, ct);

        return new PolicyDecisionResponse
        {
            Denied = decision.Denied, Policy = decision.PolicyName, Reason = decision.Reason,
        };
    }

    private static string RequireRule(JsonElement? rule)
    {
        if (rule is not { } r || r.ValueKind != JsonValueKind.Object)
            throw new ValidationException("rule must be a JSON object");

        // Reject an action this build cannot enforce, at write time. An admin who
        // typed "warn" would otherwise save a policy that silently does nothing and
        // believe the system is guarded.
        if (r.TryGetProperty("action", out var action)
            && action.ValueKind == JsonValueKind.String
            && !PolicyEntity.Actions.Contains(action.GetString(), StringComparer.OrdinalIgnoreCase))
            throw new ValidationException(
                $"action must be one of: {string.Join(", ", PolicyEntity.Actions)}");

        return r.GetRawText();
    }

    private async Task<PolicyEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.Policies.FirstOrDefaultAsync(p => p.PolicyId == id && p.IsActive, ct);
        if (row is null) throw new NotFoundException("policy not found");
        return row;
    }

    private static PolicyResponse ToResponse(PolicyEntity p) => new()
    {
        Id = p.PolicyId,
        Name = p.Name,
        Description = p.Description,
        Rule = Parse(p.RuleJson),
        Enabled = p.Enabled,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };

    private static JsonElement Parse(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }
}
