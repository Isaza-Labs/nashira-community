using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using RunTrigger = nashira_backend.Data.Models.RunTrigger;
using nashira_backend.Data.DTos.Workflow;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Workflow;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Canonical workflow CRUD. Read = viewer; write = operator+. On write, nodes/edges are
// validated against workflow.v1 + required acyclic, and the SchemaHash is recomputed. Only
// draft workflows are editable; a structural edit clears the (now stale) simulation link.
[ApiController]
[Route("api/workflows")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class WorkflowController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly WorkflowValidator _validator;
    private readonly WorkflowReferenceChecker _references;
    private readonly WorkflowSimulationService _simulation;
    private readonly PromotionService _promotion;
    private readonly WorkflowYamlCompiler _compiler;
    private readonly WorkflowYamlParser _parser;
    private readonly WorkflowRunService _runService;
    private readonly IWorkflowBundleService _bundles;
    private readonly NodeTierResolver _tiers;

    public WorkflowController(
        AppDbContext db, ICurrentUser user, WorkflowValidator validator,
        WorkflowReferenceChecker references,
        WorkflowSimulationService simulation, PromotionService promotion, WorkflowYamlCompiler compiler,
        WorkflowYamlParser parser, WorkflowRunService runService, IWorkflowBundleService bundles,
        NodeTierResolver tiers)
    {
        _db = db;
        _user = user;
        _validator = validator;
        _references = references;
        _simulation = simulation;
        _promotion = promotion;
        _compiler = compiler;
        _parser = parser;
        _runService = runService;
        _bundles = bundles;
        _tiers = tiers;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<WorkflowSummary>>> Get(
        string? environment = null, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.Workflows.AsNoTracking().Where(w => w.IsActive);
        if (!string.IsNullOrWhiteSpace(environment)) q = q.Where(w => w.Environment == environment);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(w => w.UpdatedAt).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<WorkflowSummary>
        {
            Items = rows.Select(ToSummary).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WorkflowResponse>> GetById(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<WorkflowResponse>> Post([FromBody] CreateWorkflow dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        RequireArray(dto.Nodes, "nodes");
        RequireArray(dto.Edges, "edges");
        var hash = _validator.ValidateAndHash(dto.Nodes, dto.Edges);
        await _references.EnsureResolvableAsync(dto.Nodes, ct);
        await EnsureConversationAsync(dto.ConversationId, ct);

        var now = DateTime.UtcNow;
        var row = new WorkflowEntity
        {
            WorkflowId = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            Description = dto.Description,
            Version = 1,
            SchemaVersion = "v1",
            NodesJson = dto.Nodes.GetRawText(),
            EdgesJson = dto.Edges.GetRawText(),
            InputSchemaJson = dto.InputSchema?.GetRawText(),
            MetadataJson = dto.Metadata?.GetRawText(),
            Environment = WorkflowEntity.EnvDraft,
            SchemaHash = hash,
            ChangeSummary = dto.ChangeSummary ?? string.Empty,
            CreatedByUserId = _user.IsAuthenticated ? _user.UserId : (Guid?)null,
            ConversationId = dto.ConversationId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Workflows.Add(row);
        await _db.SaveChangesAsync(ct);
        return new CreatedAtActionResult(nameof(GetById), "Workflow", new { id = row.WorkflowId }, ToResponse(row));
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<WorkflowResponse>> Update(Guid id, [FromBody] UpdateWorkflow dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        if (row.Environment != WorkflowEntity.EnvDraft)
            throw new ForbiddenException("only draft workflows are editable; promote a copy or create a new draft");

        if (dto.Nodes is not null || dto.Edges is not null)
        {
            var nodes = dto.Nodes ?? Parse(row.NodesJson);
            var edges = dto.Edges ?? Parse(row.EdgesJson);
            RequireArray(nodes, "nodes");
            RequireArray(edges, "edges");
            var hash = _validator.ValidateAndHash(nodes, edges);
            await _references.EnsureResolvableAsync(nodes, ct);
            row.NodesJson = nodes.GetRawText();
            row.EdgesJson = edges.GetRawText();
            // The simulation link is kept even when the hash changes: the promotion gate
            // recomputes and reports simulation_stale (oracle semantics), not _missing.
            row.SchemaHash = hash;

            // Acceptance verdicts, by contrast, are cleared outright. They were
            // measured by executing a different definition, so keeping them would let
            // an "all tests green" gate pass on evidence for code that no longer
            // exists — the one failure mode the tests are there to prevent.
            var tests = await _db.WorkflowAcceptanceTests
                .Where(t => t.WorkflowId == id && t.IsActive && t.LastStatus != null)
                .ToListAsync(ct);
            foreach (var t in tests)
            {
                t.LastStatus = null;
                t.LastFailuresJson = null;
                t.UpdatedAt = DateTime.UtcNow;
            }
        }

        if (dto.Name is not null && dto.Name.Trim().Length > 0) row.Name = dto.Name.Trim();
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.InputSchema is not null) row.InputSchemaJson = dto.InputSchema.Value.GetRawText();
        if (dto.Metadata is not null) row.MetadataJson = dto.Metadata.Value.GetRawText();
        if (dto.ChangeSummary is not null) row.ChangeSummary = dto.ChangeSummary;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<WorkflowResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    [HttpPost("{id:guid}/simulate")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    [SkipAudit] // operation endpoint (creates a SimulationResult), not a workflow CRUD mutation
    public async Task<ActionResult<SimulationResultResponse>> Simulate(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var sim = await _simulation.SimulateAsync(row, ct);
        return new SimulationResultResponse
        {
            SimulationResultId = sim.SimulationResultId,
            WorkflowId = sim.WorkflowId,
            Ok = sim.Ok,
            NodeCount = sim.NodeCount,
            IssueCount = sim.IssueCount,
            WarningCount = sim.WarningCount,
            SchemaHash = sim.SchemaHash,
            Issues = Parse(sim.IssuesJson),
            Warnings = Parse(sim.WarningsJson),
            SimulatedAt = sim.CreatedAt,
        };
    }

    [HttpPost("{id:guid}/promote")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    [SkipAudit] // PromotionService emits the workflow/promote audit event
    public async Task<ActionResult<WorkflowResponse>> Promote(
        Guid id, [FromBody] PromoteWorkflowRequest req, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var promoted = await _promotion.PromoteAsync(row, req.Target, req.ApprovedBy, req.ChangeSummary, ct);
        return ToResponse(promoted);
    }

    // The exportable artifact. `download=true` adds a Content-Disposition so a browser
    // saves it as a file instead of rendering it — the API shape stays identical, so
    // scripted callers are unaffected.
    [HttpGet("{id:guid}/yaml")]
    public async Task<IActionResult> Yaml(Guid id, bool download = false, CancellationToken ct = default)
    {
        var row = await Find(id, ct);
        var yaml = _compiler.Compile(row);
        if (!download) return Content(yaml, "application/yaml");

        return File(System.Text.Encoding.UTF8.GetBytes(yaml), "application/yaml", FileNameFor(row));
    }

    // The portable artifact — the only export that survives crossing instances.
    //
    // The YAML above carries nodes verbatim, which means it carries nothing but this
    // instance's GUIDs; on a second instance none of them resolve and the import is
    // refused by the same reference gate that protects POST /api/workflows. A bundle
    // adds the definition of every snippet the nodes name, so the receiving instance
    // can recreate what it does not have instead of rejecting the file.
    //
    // Credentials never travel: integrations, MCP servers, credentials and repositories
    // are named, not described. Sub-workflows and triggers travel (without secrets or
    // targets), and `requires` says what the receiving instance must support.
    [HttpGet("{id:guid}/bundle")]
    public async Task<IActionResult> Bundle(Guid id, bool download = true, CancellationToken ct = default)
    {
        var row = await Find(id, ct);
        var bundle = await _bundles.BuildAsync(id, ct);
        var json = JsonSerializer.Serialize(bundle, WorkflowBundleReader.SerializerOptions);
        if (!download) return Content(json, "application/json");
        return File(
            System.Text.Encoding.UTF8.GetBytes(json), "application/json",
            WorkflowBundleReader.FileName(row.Name));
    }

    // Imports a compiled workflow artifact as a new draft.
    //
    // Always a new row in `draft`, never an update: the document's own id and
    // environment are ignored. Importing onto an existing id would let a file
    // overwrite an unrelated local workflow, and importing straight into production
    // would bypass the promotion gate — the two properties this endpoint has to keep.
    [HttpPost("import")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<WorkflowResponse>> Import(
        [FromBody] ImportWorkflow dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Content))
            throw new ValidationException("content is required");

        // A bundle announces itself by `kind`. Anything else is the plain YAML/JSON
        // artifact and keeps the behaviour it always had.
        if (WorkflowBundleReader.LooksLikeBundle(dto.Content))
            return await ImportBundleAsync(dto, ct);

        var parsed = _parser.Parse(dto.Content);
        // Same write-time gate as POST /api/workflows: workflow.v1 + acyclic. An
        // import is not a trusted path just because the file came from an export.
        var hash = _validator.ValidateAndHash(parsed.Nodes, parsed.Edges);
        await _references.EnsureResolvableAsync(parsed.Nodes, ct);

        var name = string.IsNullOrWhiteSpace(dto.Name) ? parsed.Name : dto.Name.Trim();
        var now = DateTime.UtcNow;
        var row = new WorkflowEntity
        {
            WorkflowId = Guid.NewGuid(),
            Name = name,
            Description = parsed.Description,
            Version = 1,
            SchemaVersion = string.IsNullOrWhiteSpace(parsed.SchemaVersion) ? "v1" : parsed.SchemaVersion!,
            NodesJson = parsed.Nodes.GetRawText(),
            EdgesJson = parsed.Edges.GetRawText(),
            InputSchemaJson = parsed.InputSchema?.GetRawText(),
            MetadataJson = parsed.Metadata?.GetRawText(),
            Environment = WorkflowEntity.EnvDraft,
            SchemaHash = hash,
            // No simulation link: the definition is new to this instance, so it has
            // never been simulated here and the promotion gate must say so.
            LastSimulationId = null,
            ChangeSummary = dto.ChangeSummary ?? "imported",
            CreatedByUserId = _user.IsAuthenticated ? _user.UserId : (Guid?)null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Workflows.Add(row);
        await _db.SaveChangesAsync(ct);
        return new CreatedAtActionResult(nameof(GetById), "Workflow", new { id = row.WorkflowId }, ToResponse(row));
    }

    // Deterministic path for a bundle exported by another workflow.v1 instance
    // (workflow-v1-conformance/bundle/SPEC.md): resolve every dependency by identity,
    // create the snippets and sub-workflows the bundle carries, rewrite the node
    // references, create the triggers disabled, then go through the SAME validation as
    // every other write so schema, acyclicity and reference resolution still apply.
    //
    // Resolution throws listing everything that is missing, unsupported or
    // untranslatable — at once — rather than substituting anything. The whole point of
    // the format is that a shared workflow either arrives intact or says exactly why it
    // cannot: a stub that returns nothing produces a run that goes green having done no
    // work, which is worse than a refused import.
    private async Task<ActionResult<WorkflowResponse>> ImportBundleAsync(
        ImportWorkflow dto, CancellationToken ct)
    {
        var bundle = WorkflowBundleReader.Parse(dto.Content!);
        var imported = await _bundles.ImportAsync(bundle, dto.Name, dto.ChangeSummary, ct);
        var row = imported.Workflow;

        var response = ToResponse(row);
        // What the translation cost, if anything: a dropped network flag, a target_mode
        // with no local equivalent, a snippet matched by name rather than slug, a secret
        // that does not exist here yet, a trigger waiting for its targets. Silent here
        // would mean the operator finds out from a run.
        response.ImportNotes = imported.Notes.Count > 0 ? imported.Notes.ToList() : null;
        response.CreatedSnippets = imported.CreatedSnippets
            .Select(sn => new ImportedSnippet { SnippetId = sn.SnippetId, Name = sn.Name, Type = sn.Type })
            .ToList();
        response.CreatedWorkflows = imported.CreatedWorkflows
            .Select(w => new ImportedWorkflow { WorkflowId = w.WorkflowId, Name = w.Name })
            .ToList();
        response.CreatedTriggers = imported.CreatedTriggers
            .Select(t => new ImportedTrigger
            {
                WorkflowTriggerId = t.WorkflowTriggerId, Name = t.Name, Type = t.Type, Route = t.Route,
            })
            .ToList();

        return new CreatedAtActionResult(nameof(GetById), "Workflow", new { id = row.WorkflowId }, response);
    }

    // Slug the workflow name into a safe download filename.
    private static string FileNameFor(WorkflowEntity w)
    {
        var chars = w.Name
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.Length == 0) slug = "workflow";
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return $"{slug}-v{w.Version}-{w.Environment}.yaml";
    }

    // Immutable snapshots of what was promoted, newest first. Answers "what exactly
    // ran in production last month", which the live row cannot.
    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<object>> Versions(Guid id, CancellationToken ct)
    {
        await Find(id, ct);
        var rows = await _db.WorkflowVersions.AsNoTracking()
            .Where(v => v.WorkflowId == id && v.IsActive)
            .OrderByDescending(v => v.Version)
            .ToListAsync(ct);

        return new OkObjectResult(new
        {
            items = rows.Select(v => new
            {
                workflow_version_id = v.WorkflowVersionId,
                version = v.Version,
                environment = v.Environment,
                schema_hash = v.SchemaHash,
                change_summary = v.ChangeSummary,
                promoted_at = v.PromotedAt,
                promoted_by = v.PromotedBy,
                simulation_id = v.SimulationId,
            }),
            total = rows.Count,
        });
    }

    // One snapshot in full, including its nodes and edges — what a rollback would
    // restore, shown before it is restored.
    [HttpGet("versions/{versionId:guid}")]
    public async Task<ActionResult<object>> Version(Guid versionId, CancellationToken ct)
    {
        var v = await _db.WorkflowVersions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowVersionId == versionId && x.IsActive, ct);
        if (v is null) throw new NotFoundException("workflow version not found");

        return new OkObjectResult(new
        {
            workflow_version_id = v.WorkflowVersionId,
            workflow_id = v.WorkflowId,
            version = v.Version,
            environment = v.Environment,
            schema_hash = v.SchemaHash,
            change_summary = v.ChangeSummary,
            promoted_at = v.PromotedAt,
            promoted_by = v.PromotedBy,
            nodes = Parse(v.NodesJson),
            edges = Parse(v.EdgesJson),
            input_schema = v.InputSchemaJson is null ? null : (JsonElement?)Parse(v.InputSchemaJson),
            metadata = v.MetadataJson is null ? null : (JsonElement?)Parse(v.MetadataJson),
        });
    }

    // Copies a past snapshot back onto the live draft. A new draft rather than an
    // in-place overwrite of whatever environment it came from: a rollback must go
    // through the same promotion gate as any other change, or it becomes a way to
    // put unsimulated code into production.
    [HttpPost("versions/{versionId:guid}/restore")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<WorkflowResponse>> RestoreVersion(Guid versionId, CancellationToken ct)
    {
        var v = await _db.WorkflowVersions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowVersionId == versionId && x.IsActive, ct);
        if (v is null) throw new NotFoundException("workflow version not found");

        var source = await _db.Workflows.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkflowId == v.WorkflowId && w.IsActive, ct);

        var now = DateTime.UtcNow;
        var draft = new WorkflowEntity
        {
            WorkflowId = Guid.NewGuid(),
            Name = source?.Name ?? $"restored-{v.Version}",
            Description = source?.Description,
            Version = 1,
            SchemaVersion = source?.SchemaVersion ?? "v1",
            NodesJson = v.NodesJson,
            EdgesJson = v.EdgesJson,
            InputSchemaJson = v.InputSchemaJson,
            MetadataJson = v.MetadataJson,
            Environment = WorkflowEntity.EnvDraft,
            SchemaHash = v.SchemaHash,
            // Never carried over: this definition has not been simulated *here*, and
            // the gate has to say so.
            LastSimulationId = null,
            ChangeSummary = $"restored from version {v.Version} ({v.Environment})",
            CreatedByUserId = _user.IsAuthenticated ? _user.UserId : null,
            ConversationId = v.ConversationId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Workflows.Add(draft);
        await _db.SaveChangesAsync(ct);
        return ToResponse(draft);
    }

    [HttpGet("{id:guid}/plan")]
    public async Task<ActionResult<WorkflowPlanResponse>> Plan(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var dag = Dag.Parse(Parse(row.NodesJson), Parse(row.EdgesJson));
        // The tiers come from the snippets the nodes name, not from the nodes: the
        // plan has to answer what the run will actually do. A `subflow` node's answer
        // comes from the child's snippets, transitively — the workflow id is what lets
        // the resolver refuse a chain that re-enters this one.
        var tiers = await _tiers.ResolveAsync(dag, row.WorkflowId, ct);
        var rollback = WorkflowRollbackAnalyzer.Analyze(dag, tiers.Tiers);
        return new WorkflowPlanResponse
        {
            Order = dag.TopologicalOrder().ToList(),
            RollbackReversible = rollback.Reversible,
            NonReversibleNodes = rollback.NonReversibleNodes.ToList(),
            UnresolvableNodes = tiers.Unresolvable
                .Select(u => new PlanUnresolvableNode { NodeId = u.NodeId, Code = u.Code, Reason = u.Reason })
                .ToList(),
        };
    }

    [HttpPost("{id:guid}/run")]
    [EnableRateLimiting(RateLimitingConfiguration.WorkflowRun)]
    [Authorize(Policy = "Operator")]
    [SkipAudit] // the run emits its own audit.v1 events per node mutation
    public async Task<ActionResult<WorkflowRunResponse>> Run(
        Guid id, [FromBody] RunWorkflowRequest? req, CancellationToken ct)
    {
        var row = await Find(id, ct);

        var input = req?.Input;
        if (input is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
            throw new ValidationException("input must be a JSON object");

        var targets = req?.TargetDevices?.Distinct().ToList() ?? [];
        if (targets.Count > 0)
        {
            // Reject an unknown id up front. Discovering it mid-run would leave a
            // half-executed workflow whose failure names a device rather than the
            // request that was wrong.
            var known = await _db.Devices.AsNoTracking()
                .Where(d => d.IsActive && targets.Contains(d.DeviceId))
                .Select(d => d.DeviceId)
                .ToListAsync(ct);
            var missing = targets.Except(known).ToList();
            if (missing.Count > 0)
                throw new ValidationException($"unknown target device(s): {string.Join(", ", missing)}");
        }

        return ToRunResponse(await _runService.RunAsync(row, input, targets, ct, RunTrigger.Manual));
    }

    [HttpGet("{id:guid}/runs")]
    public async Task<ActionResult<ListResponse<WorkflowRunResponse>>> Runs(
        Guid id, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.WorkflowRuns.AsNoTracking().Where(r => r.WorkflowId == id);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.StartedAt).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<WorkflowRunResponse>
        {
            Items = rows.Select(ToRunResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<WorkflowRunDetailResponse>> RunDetail(Guid runId, CancellationToken ct)
    {
        var run = await _db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.WorkflowRunId == runId, ct);
        if (run is null) throw new NotFoundException("workflow run not found");

        var steps = await _db.StepRuns.AsNoTracking()
            .Where(s => s.WorkflowRunId == runId)
            .OrderBy(s => s.Sequence)
            .ToListAsync(ct);

        var detail = new WorkflowRunDetailResponse
        {
            Steps = steps.Select(s => new StepRunResponse
            {
                NodeId = s.NodeId, Sequence = s.Sequence, Result = s.Result,
                ErrorCode = s.ErrorCode, Retryable = s.Retryable,
                Output = ParseStepOutput(s.OutputJson),
                Error = s.Error,
                ChildRunId = s.ChildRunId,
                Input = ParseStepOutput(s.InputJson),
                Logs = s.Logs,
                Attempts = s.Attempts,
                StartedAt = s.StartedAt,
                FinishedAt = s.FinishedAt,
                DurationMs = s.StartedAt is { } from && s.FinishedAt is { } to && to >= from
                    ? (int)(to - from).TotalMilliseconds
                    : null,
            }).ToList(),
        };
        FillRun(detail, run);
        return detail;
    }

    // Stored output is opaque text written by the engine. It is returned as JSON when
    // it parses and dropped when it does not: handing the client a malformed
    // fragment would make the run detail fail to render over one bad node, and the
    // rest of the run is still worth reading.
    private static JsonElement? ParseStepOutput(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WorkflowRunResponse ToRunResponse(Data.Models.WorkflowRun r)
    {
        var resp = new WorkflowRunResponse();
        FillRun(resp, r);
        return resp;
    }

    private static void FillRun(WorkflowRunResponse resp, Data.Models.WorkflowRun r)
    {
        resp.WorkflowRunId = r.WorkflowRunId;
        resp.WorkflowId = r.WorkflowId;
        resp.Environment = r.Environment;
        resp.Status = r.Status;
        resp.FinalState = r.FinalState;
        resp.NodeCount = r.NodeCount;
        resp.ChangedCount = r.ChangedCount;
        resp.FailedCount = r.FailedCount;
        resp.RollbackPlan = Parse(r.RollbackPlanJson);
        resp.Input = r.InputJson is null ? null : Parse(r.InputJson);
        resp.TargetDevices = Parse(string.IsNullOrWhiteSpace(r.TargetDevicesJson) ? "[]" : r.TargetDevicesJson);
        resp.StartedAt = r.StartedAt;
        resp.FinishedAt = r.FinishedAt;
        resp.Trigger = r.Trigger;
        resp.ParentRunId = r.ParentRunId;
        resp.Error = r.Error;
    }

    private static void RequireArray(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Array) throw new ValidationException($"{name} must be an array");
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private async Task<WorkflowEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.Workflows.FirstOrDefaultAsync(
            w => w.WorkflowId == id && w.IsActive, ct);
        if (row is null) throw new NotFoundException("workflow not found");
        return row;
    }

    // A client-supplied conversation_id must name a real conversation, otherwise the
    // provenance link is a dangling id that looks authoritative in the UI.
    private async Task EnsureConversationAsync(Guid? conversationId, CancellationToken ct)
    {
        if (conversationId is not { } cid) return;
        var exists = await _db.AIConversations.AnyAsync(c => c.AIConversationId == cid && c.IsActive, ct);
        if (!exists) throw new ValidationException("conversation_id does not exist");
    }

    private static WorkflowSummary ToSummary(WorkflowEntity w) => new()
    {
        WorkflowId = w.WorkflowId,
        Name = w.Name,
        Version = w.Version,
        Environment = w.Environment,
        SchemaHash = w.SchemaHash,
        LastSimulationId = w.LastSimulationId,
        ConversationId = w.ConversationId,
        UpdatedAt = w.UpdatedAt,
    };

    private static WorkflowResponse ToResponse(WorkflowEntity w) => new()
    {
        WorkflowId = w.WorkflowId,
        Name = w.Name,
        Description = w.Description,
        Version = w.Version,
        SchemaVersion = w.SchemaVersion,
        Environment = w.Environment,
        SchemaHash = w.SchemaHash,
        LastSimulationId = w.LastSimulationId,
        ConversationId = w.ConversationId,
        Nodes = Parse(w.NodesJson),
        Edges = Parse(w.EdgesJson),
        InputSchema = w.InputSchemaJson is null ? null : Parse(w.InputSchemaJson),
        Metadata = w.MetadataJson is null ? null : Parse(w.MetadataJson),
        PromotedFrom = w.PromotedFrom,
        ChangeSummary = w.ChangeSummary,
        CreatedAt = w.CreatedAt,
        UpdatedAt = w.UpdatedAt,
    };
}
