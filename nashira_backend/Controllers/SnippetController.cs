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
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;
using SnippetEntity = nashira_backend.Data.Models.Snippet;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

public class CreateSnippet
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("script_language")] public string? ScriptLanguage { get; set; }
    [JsonPropertyName("input_schema")] public string? InputSchema { get; set; }
    [JsonPropertyName("output_schema")] public string? OutputSchema { get; set; }
    [JsonPropertyName("target_mode")] public string? TargetMode { get; set; }
    [JsonPropertyName("timeout_seconds")] public int? TimeoutSeconds { get; set; }
    [JsonPropertyName("idempotency")] public string? Idempotency { get; set; }
    // Whether a step running this snippet changes anything. Required in practice for the
    // types whose code this row carries and whose handler therefore cannot tell — a
    // `python_snippet` with neither this nor `config_overrides.changes` on the node FAILS
    // its step rather than defaulting.
    [JsonPropertyName("changes_state")] public bool? ChangesState { get; set; }
    [JsonPropertyName("logic_diagram_mermaid")] public string? LogicDiagramMermaid { get; set; }
    [JsonPropertyName("network_enabled")] public bool? NetworkEnabled { get; set; }
}

public class UpdateSnippet : CreateSnippet
{
    [JsonPropertyName("verified")] public bool? Verified { get; set; }
    [JsonPropertyName("is_active")] public bool? IsActive { get; set; }
}

public class SnippetResponse
{
    [JsonPropertyName("snippet_id")] public Guid SnippetId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("script_language")] public string? ScriptLanguage { get; set; }
    [JsonPropertyName("input_schema")] public string? InputSchema { get; set; }
    [JsonPropertyName("output_schema")] public string? OutputSchema { get; set; }
    [JsonPropertyName("target_mode")] public string TargetMode { get; set; } = "once";
    [JsonPropertyName("timeout_seconds")] public int TimeoutSeconds { get; set; }
    // What the executor will actually apply: the declared tier after the handler's
    // ceiling. Surfaced because a snippet declaring `idempotent` over a
    // NonReversible handler is silently overruled, and the author should see that.
    [JsonPropertyName("idempotency")] public string? Idempotency { get; set; }
    [JsonPropertyName("effective_idempotency")] public string EffectiveIdempotency { get; set; } = string.Empty;
    // Null reads as "the author has not said". For a type whose handler can measure its own
    // effect that is fine and expected; for `python_snippet` or a deferring
    // `ansible_playbook` it means every step will fail until a node declares it.
    [JsonPropertyName("changes_state")] public bool? ChangesState { get; set; }
    [JsonPropertyName("logic_diagram_mermaid")] public string? LogicDiagramMermaid { get; set; }
    [JsonPropertyName("network_enabled")] public bool NetworkEnabled { get; set; }
    [JsonPropertyName("verified")] public bool Verified { get; set; }
    [JsonPropertyName("is_active")] public bool IsActive { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

// The catalog of reusable steps a workflow node invokes by `snippet_id`.
//
// Reads are Viewer (the builder has to browse them); writes are Operator, matching
// workflow authoring. A snippet is a definition, not an execution — running one is
// gated separately, by the workflow's own promotion path.
[ApiController]
[Route("api/snippets")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class SnippetController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISnippetHandlerRegistry _registry;
    private readonly IServiceProvider _sp;
    private readonly SnippetCatalog _catalog;

    public SnippetController(
        AppDbContext db, ICurrentUser user, ISnippetHandlerRegistry registry, IServiceProvider sp,
        SnippetCatalog catalog)
    {
        _db = db;
        _user = user;
        _registry = registry;
        _sp = sp;
        _catalog = catalog;
    }

    // The handler types this build can execute. The builder needs it to offer a
    // type picker that cannot produce an unrunnable snippet.
    [HttpGet("types")]
    public ActionResult<IEnumerable<string>> Types() =>
        new OkObjectResult(_registry.KnownTypes.OrderBy(t => t, StringComparer.Ordinal));

    [HttpGet]
    public async Task<ActionResult<ListResponse<SnippetResponse>>> Get(
        string? type = null, string? q = null, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var availableTypes = _registry.KnownTypes.ToArray();
        var query = _db.Snippets.AsNoTracking()
            .Where(s => s.IsActive && availableTypes.Contains(s.Type));
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(s => s.Type == type);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q}%";
            query = query.Where(s =>
                EF.Functions.ILike(s.Name, pattern)
                || (s.Description != null && EF.Functions.ILike(s.Description, pattern)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(s => s.Name).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<SnippetResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SnippetResponse>> GetById(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<SnippetResponse>> Post([FromBody] CreateSnippet dto, CancellationToken ct)
    {
        // Validation, slug allocation and the admin gate on network_enabled all live
        // in SnippetCatalog, shared with the agent's `create_snippet` tool and with
        // bundle import — so the three routes cannot diverge on what they accept.
        var row = await _catalog.CreateAsync(
            new SnippetDraft(
                Name: dto.Name,
                Type: dto.Type,
                Description: dto.Description,
                Code: dto.Code,
                ScriptLanguage: dto.ScriptLanguage,
                InputSchemaJson: dto.InputSchema,
                OutputSchemaJson: dto.OutputSchema,
                TargetMode: dto.TargetMode,
                TimeoutSeconds: dto.TimeoutSeconds,
                Idempotency: dto.Idempotency,
                LogicDiagramMermaid: dto.LogicDiagramMermaid,
                NetworkEnabled: dto.NetworkEnabled,
                ChangesState: dto.ChangesState),
            ct);
        return new CreatedAtActionResult(nameof(GetById), "Snippet", new { id = row.SnippetId }, ToResponse(row));
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<SnippetResponse>> Update(
        Guid id, [FromBody] UpdateSnippet dto, CancellationToken ct)
    {
        var row = await Find(id, ct);

        if (dto.Name is not null && dto.Name.Trim().Length > 0)
        {
            var name = dto.Name.Trim();
            if (await _db.Snippets.AnyAsync(s => s.Name == name && s.IsActive && s.SnippetId != id, ct))
                throw new ConflictException("a snippet with this name already exists", "snippet_name_taken");
            row.Name = name; // slug stays put — see Snippet.Slug
        }
        // Type is editable, but it re-points the snippet at a different handler and
        // therefore a different input contract; the workflows referencing it are not
        // revalidated here, which is why the type must at least be one that exists.
        if (!string.IsNullOrWhiteSpace(dto.Type)) row.Type = _catalog.RequireKnownType(dto.Type);
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.Code is not null) row.Code = dto.Code;
        if (dto.ScriptLanguage is not null) row.ScriptLanguage = dto.ScriptLanguage;
        if (dto.InputSchema is not null) { SnippetCatalog.RequireJson(dto.InputSchema, "input_schema"); row.InputSchemaJson = dto.InputSchema; }
        if (dto.OutputSchema is not null) { SnippetCatalog.RequireJson(dto.OutputSchema, "output_schema"); row.OutputSchemaJson = dto.OutputSchema; }
        if (dto.TargetMode is not null) row.TargetMode = SnippetCatalog.NormalizeTargetMode(dto.TargetMode);
        if (dto.TimeoutSeconds is > 0) row.TimeoutSeconds = dto.TimeoutSeconds.Value;
        if (dto.Idempotency is not null) row.Idempotency = SnippetCatalog.NormalizeIdempotency(dto.Idempotency);
        if (dto.ChangesState is not null) row.ChangesState = dto.ChangesState.Value;
        // Against the MERGED values: an update that changes the type, or clears the
        // diagram, has to be judged on what the row would BECOME rather than on what the
        // request happens to carry.
        var mergedType = dto.Type ?? row.Type;
        var mergedDiagram = dto.LogicDiagramMermaid ?? row.LogicDiagramMermaid;
        if (SnippetCatalog.ValidateLogicDiagram(mergedType, mergedDiagram) is { } diagramError)
            throw new ValidationException(diagramError, "logic_diagram_invalid");

        if (dto.LogicDiagramMermaid is not null) row.LogicDiagramMermaid = dto.LogicDiagramMermaid;
        if (dto.NetworkEnabled is not null)
            row.NetworkEnabled = _catalog.RequireAdminForNetwork(dto.NetworkEnabled.Value, row.NetworkEnabled);
        if (dto.Verified is not null) row.Verified = dto.Verified.Value;
        if (dto.IsActive is not null) row.IsActive = dto.IsActive.Value;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<SnippetResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        // Refuse while a live workflow still names it: the node would resolve to
        // nothing at run time, and "unknown snippet" mid-run is a worse discovery
        // than a rejected delete.
        var referencing = await _db.Workflows.AsNoTracking()
            .Where(w => w.IsActive && w.NodesJson.Contains(id.ToString()))
            .Select(w => w.Name)
            .Take(5)
            .ToListAsync(ct);
        if (referencing.Count > 0)
            throw new ConflictException(
                $"still referenced by: {string.Join(", ", referencing)}", "snippet_in_use");

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    private async Task<SnippetEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.Snippets.FirstOrDefaultAsync(s => s.SnippetId == id && s.IsActive, ct);
        if (row is null
            || !_registry.KnownTypes.Contains(row.Type, StringComparer.OrdinalIgnoreCase))
            throw new NotFoundException("snippet not found");
        return row;
    }

    private SnippetResponse ToResponse(SnippetEntity s)
    {
        var handler = _registry.Resolve(s.Type, _sp);
        var floor = handler?.DefaultIdempotency ?? IdempotencyKind.RequiresCompensation;
        return new SnippetResponse
        {
            SnippetId = s.SnippetId,
            Name = s.Name,
            Slug = s.Slug,
            Type = s.Type,
            Description = s.Description,
            Code = s.Code,
            ScriptLanguage = s.ScriptLanguage,
            InputSchema = s.InputSchemaJson,
            OutputSchema = s.OutputSchemaJson,
            TargetMode = s.TargetMode,
            TimeoutSeconds = s.TimeoutSeconds,
            Idempotency = s.Idempotency,
            EffectiveIdempotency = Idempotency.ToWire(Idempotency.Effective(s, floor)),
            ChangesState = s.ChangesState,
            LogicDiagramMermaid = s.LogicDiagramMermaid,
            NetworkEnabled = s.NetworkEnabled,
            Verified = s.Verified,
            IsActive = s.IsActive,
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt,
        };
    }
}
