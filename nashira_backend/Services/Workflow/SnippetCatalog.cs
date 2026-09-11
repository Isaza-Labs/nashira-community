using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Worker;
using IdempotencyTier = nashira_backend.Services.Workflow.Idempotency;
using SnippetEntity = nashira_backend.Data.Models.Snippet;

namespace nashira_backend.Services.Workflow;

// What a caller supplies to create a snippet, independent of how it arrived.
//
// The same fields reach the catalogue from three directions now — the REST
// controller, the agent's `create_snippet` tool and a bundle import — and each
// used to be free to skip a check the others made. A snippet created by one
// route that the others would have rejected is the kind of drift that only
// shows up at run time, on a device.
public sealed record SnippetDraft(
    string? Name,
    string? Type,
    string? Description = null,
    string? Code = null,
    string? ScriptLanguage = null,
    string? InputSchemaJson = null,
    string? OutputSchemaJson = null,
    string? TargetMode = null,
    int? TimeoutSeconds = null,
    string? Idempotency = null,
    string? LogicDiagramMermaid = null,
    bool? NetworkEnabled = null,
    // Whether a step running this snippet changes anything, for the types whose CODE this
    // row carries — a python script, a playbook. Null means the author has not said, which
    // is only legal for types whose handler can answer for itself.
    bool? ChangesState = null);

// The one place a snippet row is validated and built.
//
// Extracted from SnippetController when `create_snippet` became a native agent
// tool: the alternative was a second copy of the type check, the name-collision
// check, the slug allocation and the admin gate on `network_enabled`, kept in
// step by hand. The controller now delegates here, so the agent cannot create a
// snippet the API would have refused, and vice versa.
public sealed class SnippetCatalog
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISnippetHandlerRegistry _registry;

    public SnippetCatalog(AppDbContext db, ICurrentUser user, ISnippetHandlerRegistry registry)
    {
        _db = db;
        _user = user;
        _registry = registry;
    }

    // Builds and persists the row. Throws the same ValidationException /
    // ConflictException / ForbiddenException the controller always threw, so the
    // HTTP surface is unchanged and the tool handler can translate them into a
    // structured refusal the agent relays verbatim.
    public async Task<SnippetEntity> CreateAsync(SnippetDraft draft, CancellationToken ct)
    {
        // Here and not in BuildAsync: that one is shared with the bundle importer, and
        // FlowWeaver's importer does not validate the diagram either. Enforcing it there
        // would make the two products disagree about IMPORTS in order to make them agree
        // about the API. See this change's followups.
        if (ValidateLogicDiagram(draft.Type, draft.LogicDiagramMermaid) is { } diagramError)
            throw new ValidationException(diagramError, "logic_diagram_invalid");

        var row = await BuildAsync(draft, ct);
        _db.Snippets.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    // The row without the write, for callers that batch their own SaveChanges —
    // a bundle import creates several snippets and the workflow in one transaction.
    public async Task<SnippetEntity> BuildAsync(SnippetDraft draft, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(draft.Name)) throw new ValidationException("name is required");
        var type = RequireKnownType(draft.Type);
        RequireJson(draft.InputSchemaJson, "input_schema");
        RequireJson(draft.OutputSchemaJson, "output_schema");

        var name = draft.Name.Trim();
        if (await _db.Snippets.AnyAsync(s => s.Name == name && s.IsActive, ct))
            throw new ConflictException("a snippet with this name already exists", "snippet_name_taken");

        var taken = await TakenSlugsAsync(ct);

        var now = DateTime.UtcNow;
        return new SnippetEntity
        {
            SnippetId = Guid.NewGuid(),
            Name = name,
            Slug = Slug.Unique(name, taken.Contains),
            Type = type,
            Description = draft.Description,
            Code = draft.Code,
            ScriptLanguage = draft.ScriptLanguage,
            InputSchemaJson = draft.InputSchemaJson,
            OutputSchemaJson = draft.OutputSchemaJson,
            TargetMode = NormalizeTargetMode(draft.TargetMode),
            TimeoutSeconds = draft.TimeoutSeconds is > 0 ? draft.TimeoutSeconds.Value : 60,
            Idempotency = NormalizeIdempotency(draft.Idempotency),
            ChangesState = draft.ChangesState,
            LogicDiagramMermaid = draft.LogicDiagramMermaid,
            NetworkEnabled = RequireAdminForNetwork(draft.NetworkEnabled ?? false, currentlyEnabled: false),
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // Every slug in use, active or not: a slug is permanent identity, so a
    // deactivated snippet still owns its own.
    public async Task<HashSet<string>> TakenSlugsAsync(CancellationToken ct)
    {
        var slugs = await _db.Snippets.Select(s => s.Slug).ToListAsync(ct);
        return new HashSet<string>(slugs, StringComparer.OrdinalIgnoreCase);
    }

    // Turning network access ON widens which allowlist modules a snippet may
    // import, so it is an Admin act — while turning it OFF is a narrowing any
    // Operator may do. Asymmetric on purpose.
    public bool RequireAdminForNetwork(bool requested, bool currentlyEnabled)
    {
        if (requested && !currentlyEnabled
            && !_user.Roles.Contains("admin", StringComparer.OrdinalIgnoreCase))
            throw new ForbiddenException("enabling network access on a snippet requires an administrator");
        return requested;
    }

    public string RequireKnownType(string? raw)
    {
        var t = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (t.Length == 0) throw new ValidationException("type is required");
        if (!_registry.KnownTypes.Contains(t, StringComparer.OrdinalIgnoreCase))
            throw new ValidationException(
                $"unknown snippet type '{t}'. Known types: {string.Join(", ", _registry.KnownTypes.OrderBy(x => x))}");
        return t;
    }

    public static string NormalizeTargetMode(string? raw)
    {
        var v = (raw ?? SnippetEntity.TargetOnce).Trim().ToLowerInvariant();
        if (v.Length == 0) v = SnippetEntity.TargetOnce;
        if (v is not (SnippetEntity.TargetOnce or SnippetEntity.TargetPerDevice))
            throw new ValidationException(
                $"target_mode must be '{SnippetEntity.TargetOnce}' or '{SnippetEntity.TargetPerDevice}'");
        return v;
    }

    public static string? NormalizeIdempotency(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().ToLowerInvariant();
        if (v is not (IdempotencyTier.Idempotent
            or IdempotencyTier.RequiresCompensation
            or IdempotencyTier.NonReversible))
            throw new ValidationException(
                $"idempotency must be one of: {IdempotencyTier.Idempotent}, "
                + $"{IdempotencyTier.RequiresCompensation}, {IdempotencyTier.NonReversible}");
        return v;
    }

    /// <summary>
    /// Types whose behaviour is supplied as CODE, and which therefore have to explain
    /// themselves: a type and a name say what a `ping` does and say nothing about a
    /// `python_snippet`.
    /// </summary>
    /// <remarks>
    /// Ported verbatim from FlowWeaver's <c>SnippetService</c> — same set, same directive
    /// list, same message — because a parity rule that reworded its own refusal would leave
    /// an operator comparing two products by their error text and concluding they differ.
    ///
    /// Found by seeding both products through their own APIs with one payload: this one
    /// accepted it and the other refused it, so a `transform` authored here was not portable
    /// by the route an operator actually takes.
    /// </remarks>
    private static readonly HashSet<string> MermaidRequiredTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "python_snippet", "python", "transform", "jmespath",
    };

    // First-non-blank-line keywords that mean "this is likely valid Mermaid". Deliberately
    // shallow — full validation lives in the renderer. The goal is to reject the obvious
    // mistakes (a pasted script, an empty string, prose) before they reach the database.
    private static readonly string[] MermaidDirectives =
    {
        "graph", "flowchart", "sequenceDiagram", "stateDiagram", "stateDiagram-v2",
        "classDiagram", "erDiagram", "gantt", "journey", "gitGraph",
        "pie", "mindmap", "timeline", "quadrantChart", "requirementDiagram",
    };

    /// <summary>
    /// Null when the diagram is acceptable, the refusal message when it is not.
    /// </summary>
    /// <remarks>
    /// Two rules, and the second applies to EVERY type: a required type must have one, and
    /// any value that is supplied must actually be a diagram. A field that accepts anything
    /// eventually holds a pasted stack trace, and the first person to find out is whoever
    /// opens the renderer.
    /// </remarks>
    public static string? ValidateLogicDiagram(string? snippetType, string? diagram)
    {
        var required = !string.IsNullOrWhiteSpace(snippetType)
                       && MermaidRequiredTypes.Contains(snippetType);

        if (string.IsNullOrWhiteSpace(diagram))
        {
            return required
                ? $"logic_diagram_mermaid is required for type='{snippetType}'. "
                  + "Produce a Mermaid diagram (graph TD / flowchart / sequenceDiagram) "
                  + "that describes the task's inputs, decision branches, and outputs. "
                  + "See Skills/mermaid.md for conventions."
                : null;
        }

        string? firstLine = null;
        foreach (var raw in diagram.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("%%", StringComparison.Ordinal)) continue;   // Mermaid comment
            firstLine = line;
            break;
        }

        if (firstLine is null) return "logic_diagram_mermaid has no non-blank content";

        var matchesDirective = MermaidDirectives.Any(
            d => firstLine.StartsWith(d, StringComparison.OrdinalIgnoreCase));
        if (!matchesDirective)
        {
            return "logic_diagram_mermaid must start with a Mermaid directive "
                 + $"({string.Join(", ", MermaidDirectives.Take(6))}, …). "
                 + $"First non-blank line: '{Truncate(firstLine, 80)}'";
        }

        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public static void RequireJson(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try { using var _ = JsonDocument.Parse(json); }
        catch (JsonException) { throw new ValidationException($"{field} is not valid JSON"); }
    }
}
