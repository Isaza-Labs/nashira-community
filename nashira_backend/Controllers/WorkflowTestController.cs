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
using nashira_backend.Services.Workflow;
using TestEntity = nashira_backend.Data.Models.WorkflowAcceptanceTest;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

public class WriteAcceptanceTest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("input")] public JsonElement? Input { get; set; }
    [JsonPropertyName("target_devices")] public List<Guid>? TargetDevices { get; set; }
    [JsonPropertyName("assertions")] public JsonElement? Assertions { get; set; }
}

public class AcceptanceTestResponse
{
    [JsonPropertyName("workflow_acceptance_test_id")] public Guid Id { get; set; }
    [JsonPropertyName("workflow_id")] public Guid WorkflowId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("input")] public JsonElement Input { get; set; }
    [JsonPropertyName("target_devices")] public JsonElement TargetDevices { get; set; }
    [JsonPropertyName("assertions")] public JsonElement Assertions { get; set; }
    [JsonPropertyName("last_status")] public string? LastStatus { get; set; }
    [JsonPropertyName("last_run_id")] public Guid? LastRunId { get; set; }
    [JsonPropertyName("last_run_at")] public DateTime? LastRunAt { get; set; }
    [JsonPropertyName("last_failures")] public JsonElement LastFailures { get; set; }
    // False when the workflow has been edited since the result was obtained. The
    // gate refuses stale evidence, and the UI should say why before it does.
    [JsonPropertyName("result_is_current")] public bool ResultIsCurrent { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

// Golden-path tests bound to a workflow. A simulation proves the graph is sound;
// only a test run proves the behaviour.
[ApiController]
[Route("api/workflows/{workflowId:guid}/tests")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class WorkflowTestController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IAcceptanceTestRunner _runner;

    public WorkflowTestController(AppDbContext db, IAcceptanceTestRunner runner)
    {
        _db = db;
        _runner = runner;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<AcceptanceTestResponse>>> Get(
        Guid workflowId, CancellationToken ct)
    {
        var workflow = await FindWorkflow(workflowId, ct);
        var rows = await _db.WorkflowAcceptanceTests.AsNoTracking()
            .Where(t => t.WorkflowId == workflowId && t.IsActive)
            .OrderBy(t => t.Name).ToListAsync(ct);

        return new OkObjectResult(new ListResponse<AcceptanceTestResponse>
        {
            Items = rows.Select(r => ToResponse(r, workflow.SchemaHash)).ToList(),
            Total = rows.Count, Limit = rows.Count, Offset = 0,
        });
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<AcceptanceTestResponse>> Post(
        Guid workflowId, [FromBody] WriteAcceptanceTest dto, CancellationToken ct)
    {
        var workflow = await FindWorkflow(workflowId, ct);
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        RequireArray(dto.Assertions, "assertions");

        var now = DateTime.UtcNow;
        var row = new TestEntity
        {
            WorkflowAcceptanceTestId = Guid.NewGuid(),
            WorkflowId = workflowId,
            Name = dto.Name.Trim(),
            Description = dto.Description,
            InputJson = dto.Input?.GetRawText() ?? "{}",
            TargetDevicesJson = JsonSerializer.Serialize(dto.TargetDevices ?? []),
            AssertionsJson = dto.Assertions?.GetRawText() ?? "[]",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.WorkflowAcceptanceTests.Add(row);
        await _db.SaveChangesAsync(ct);
        return ToResponse(row, workflow.SchemaHash);
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<AcceptanceTestResponse>> Update(
        Guid workflowId, Guid id, [FromBody] WriteAcceptanceTest dto, CancellationToken ct)
    {
        var workflow = await FindWorkflow(workflowId, ct);
        var row = await Find(workflowId, id, ct);

        if (dto.Name is not null && dto.Name.Trim().Length > 0) row.Name = dto.Name.Trim();
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.Input is not null) row.InputJson = dto.Input.Value.GetRawText();
        if (dto.TargetDevices is not null) row.TargetDevicesJson = JsonSerializer.Serialize(dto.TargetDevices);
        if (dto.Assertions is not null)
        {
            RequireArray(dto.Assertions, "assertions");
            row.AssertionsJson = dto.Assertions.Value.GetRawText();
            // Changing what is asserted invalidates the previous verdict: it was
            // measured against different criteria.
            row.LastStatus = null;
            row.LastFailuresJson = null;
        }
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToResponse(row, workflow.SchemaHash);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<AcceptanceTestResponse>> Delete(
        Guid workflowId, Guid id, CancellationToken ct)
    {
        var workflow = await FindWorkflow(workflowId, ct);
        var row = await Find(workflowId, id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row, workflow.SchemaHash);
    }

    // Executes the test for real. Operator: it runs the workflow against devices.
    [HttpPost("{id:guid}/run")]
    [EnableRateLimiting(RateLimitingConfiguration.WorkflowRun)]
    [Authorize(Policy = "Operator")]
    [SkipAudit] // the underlying run emits its own audit events
    public async Task<ActionResult<object>> Run(Guid workflowId, Guid id, CancellationToken ct)
    {
        var workflow = await FindWorkflow(workflowId, ct);
        await Find(workflowId, id, ct);

        var outcome = await _runner.RunAsync(id, ct);
        return new OkObjectResult(new
        {
            status = outcome.Status,
            run_id = outcome.RunId,
            failures = outcome.Failures,
            schema_hash = workflow.SchemaHash,
        });
    }

    private static void RequireArray(JsonElement? value, string field)
    {
        if (value is null) return;
        if (value.Value.ValueKind != JsonValueKind.Array)
            throw new ValidationException($"{field} must be a JSON array");
    }

    private async Task<Data.Models.Workflow> FindWorkflow(Guid id, CancellationToken ct)
    {
        var row = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.WorkflowId == id && w.IsActive, ct);
        if (row is null) throw new NotFoundException("workflow not found");
        return row;
    }

    private async Task<TestEntity> Find(Guid workflowId, Guid id, CancellationToken ct)
    {
        var row = await _db.WorkflowAcceptanceTests.FirstOrDefaultAsync(
            t => t.WorkflowAcceptanceTestId == id && t.WorkflowId == workflowId && t.IsActive, ct);
        if (row is null) throw new NotFoundException("acceptance test not found");
        return row;
    }

    private static AcceptanceTestResponse ToResponse(TestEntity t, string currentSchemaHash) => new()
    {
        Id = t.WorkflowAcceptanceTestId,
        WorkflowId = t.WorkflowId,
        Name = t.Name,
        Description = t.Description,
        Input = Parse(t.InputJson, "{}"),
        TargetDevices = Parse(t.TargetDevicesJson, "[]"),
        Assertions = Parse(t.AssertionsJson, "[]"),
        LastStatus = t.LastStatus,
        LastRunId = t.LastRunId,
        LastRunAt = t.LastRunAt,
        LastFailures = Parse(t.LastFailuresJson, "[]"),
        ResultIsCurrent = t.LastStatus is not null
                          && string.Equals(t.LastSchemaHash, currentSchemaHash, StringComparison.Ordinal),
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
