using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Creates a snippet in the catalogue. Write → single_confirm.
//
// The agent's only previous route was `execute_operation` against the `na_snippets`
// spec: a self-API call whose bearer is a borrowed session token. That path carries the
// EFFECTIVE role rather than the caller's, so an admin asking for a network-enabled
// snippet got an opaque 403, and — more often here — the call never got that far,
// dying on "spec has no base_url configured". The visible result was workflows built
// out of __start__ and __end__ because the agent could not obtain a real step.
//
// This handler goes through SnippetCatalog IN-PROCESS, on the chat turn's scope, so
// the chatting user's own ICurrentUser applies. The service's RBAC still holds; what
// changes is that a refusal comes back as a structured reason the agent can relay
// verbatim instead of an HTTP status it has to guess at.
public sealed class CreateSnippetHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "name":{"type":"string","description":"Unique display name"},
          "type":{"type":"string","description":"Handler type: ping | transform | rest_call | integration_action | ssh | mcp_call | python_snippet | git | report | email_send | slack_message | ansible_playbook (netconf and snmp_v3 are reserved stubs that fail at run time)"},
          "description":{"type":"string"},
          "code":{"type":"string","description":"Body for the handlers that carry one — the script for python_snippet, the JSON mapping for transform. Omit for handlers fully described by their input."},
          "script_language":{"type":"string","description":"python, for python_snippet"},
          "input_schema":{"type":"object","description":"JSON Schema of the step input. Advisory, but it is what lets a later reader see the contract."},
          "output_schema":{"type":"object","description":"JSON Schema of the step output. Advisory."},
          "target_mode":{"type":"string","description":"once (default) or per_device. per_device fans the step out over the run's target devices."},
          "timeout_seconds":{"type":"integer","description":"Default 60"},
          "idempotency":{"type":"string","description":"idempotent | requires_compensation | non_reversible. May LOWER the handler's default; a non_reversible handler is an absolute ceiling."},
          "changes_state":{"type":"boolean","description":"Whether a step running this snippet CHANGES anything, which is a different question from idempotency: that one says whether an action could be undone, this says whether anything was done. REQUIRED for python_snippet and ansible_playbook, whose code lives on this row so the handler cannot tell. Omitting it there means every step fails until a node declares config_overrides.changes."},
          "logic_diagram_mermaid":{"type":"string","description":"Mermaid source describing what the step does"}
        },"required":["name","type"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly SnippetCatalog _catalog;
    private readonly ISnippetHandlerRegistry _registry;
    private readonly IServiceProvider _sp;
    private readonly ILogger<CreateSnippetHandler> _logger;

    public CreateSnippetHandler(
        SnippetCatalog catalog, ISnippetHandlerRegistry registry, IServiceProvider sp,
        ILogger<CreateSnippetHandler> logger)
    {
        _catalog = catalog;
        _registry = registry;
        _sp = sp;
        _logger = logger;
    }

    public string Name => "create_snippet";

    public string Description =>
        "Creates a reusable snippet — the unit a workflow node invokes by snippet_id. Call this when " +
        "list_snippets has nothing that fits, THEN build the workflow around the snippet_id it returns. " +
        "Building the workflow first and filling the reference in later does not work: create_workflow " +
        "validates every reference at write time. network_enabled is deliberately absent from this " +
        "schema — it lifts the python sandbox's network isolation, and it is set by a human from the " +
        "Snippets UI, never here. If the user needs it, say so and stop; do not look for another route.";

    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var draft = new SnippetDraft(
            Name: Str(args, "name"),
            Type: Str(args, "type"),
            Description: Str(args, "description"),
            Code: Str(args, "code"),
            ScriptLanguage: Str(args, "script_language"),
            InputSchemaJson: RawObject(args, "input_schema"),
            OutputSchemaJson: RawObject(args, "output_schema"),
            TargetMode: Str(args, "target_mode"),
            TimeoutSeconds: Int(args, "timeout_seconds"),
            Idempotency: Str(args, "idempotency"),
            ChangesState: Bool(args, "changes_state"),
            LogicDiagramMermaid: Str(args, "logic_diagram_mermaid"),
            // Never from the agent. See the class comment and the tool description.
            NetworkEnabled: false);

        Data.Models.Snippet row;
        try
        {
            row = await _catalog.CreateAsync(draft, ct);
        }
        catch (ConflictException ex)
        {
            // A name collision is recoverable and the agent should recover from it
            // rather than reporting failure: the existing snippet is very often the
            // one it wanted.
            _logger.LogInformation("ai.tool.create_snippet.conflict name={Name}", draft.Name);
            return Err(ex.Message, "snippet_name_taken",
                "A snippet with that name already exists. Call list_snippets with q=<name> to get its "
                + "snippet_id and use that, or pick a different name.");
        }
        catch (ForbiddenException ex)
        {
            _logger.LogWarning("ai.tool.create_snippet.forbidden type={Type}", draft.Type);
            return Err(ex.Message, "forbidden",
                "This requires an administrator. Ask the user to do it from the Snippets UI; do not retry.");
        }
        catch (ValidationException ex)
        {
            _logger.LogInformation("ai.tool.create_snippet.rejected type={Type}", draft.Type);
            return Err(ex.Message, "invalid", null);
        }

        var handler = _registry.Resolve(row.Type, _sp);
        var floor = handler?.DefaultIdempotency ?? IdempotencyKind.RequiresCompensation;

        _logger.LogInformation(
            "ai.tool.create_snippet.ok snippet_id={SnippetId} type={Type}", row.SnippetId, row.Type);

        return JsonSerializer.SerializeToElement(new
        {
            created = true,
            snippet_id = row.SnippetId,
            name = row.Name,
            slug = row.Slug,
            type = row.Type,
            target_mode = row.TargetMode,
            // Both tiers, because a declaration the handler's ceiling overrules is
            // otherwise silently lost — and the rollback plan reads the effective one.
            declared_idempotency = row.Idempotency,
            effective_idempotency = Idempotency.ToWire(Idempotency.Effective(row, floor)),
            // Echoed back so an omission is visible at creation rather than at the first
            // run. Null on a type whose handler measures its own effect is correct and
            // expected; null on a python_snippet is a step that will fail.
            changes_state = row.ChangesState,
            next = "Use this snippet_id as a node's snippet_id in create_workflow.",
        });
    }

    private static JsonElement Err(string message, string code, string? hint) =>
        JsonSerializer.SerializeToElement(new { created = false, error = message, code, hint });

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Only a literal true/false. Anything else is not a declaration — a string "true"
    // accepted here would record an answer the author never gave.
    private static bool? Bool(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static int? Int(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    // The schemas arrive as JSON objects and are stored as text.
    private static string? RawObject(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Object ? v.GetRawText() : null;
}
