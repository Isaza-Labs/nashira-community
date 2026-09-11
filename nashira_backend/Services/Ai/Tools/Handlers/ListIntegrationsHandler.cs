using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Integration;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Lists the configured integrations and, optionally, their action catalogs.
// Read-only → autonomous. Credentials are never included, only whether they exist.
public sealed class ListIntegrationsHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "name":{"type":"string","description":"Match name, slug or type (case-insensitive substring)"},
          "type":{"type":"string","description":"Filter by kind, e.g. netbox, servicenow"},
          "include_actions":{"type":"boolean","default":false,"description":"Include each integration's callable operations"},
          "action_keyword":{"type":"string","description":"With include_actions: substring match on action name or path"},
          "limit":{"type":"integer","minimum":1,"maximum":100,"default":25,"description":"Page size. The response reports total_matched and truncated; a truncated page is not the full set."}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private const int MaxActionsPerIntegration = 50;

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ListIntegrationsHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "list_integrations";
    public string Description =>
        "Lists configured integrations (external systems such as NetBox or ServiceNow) with their " +
        "status, and optionally the operations each one exposes. Never returns credentials.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "name");
        var type = Str(args, "type");
        var includeActions = Bool(args, "include_actions", false);
        var actionKeyword = Str(args, "action_keyword");
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv)
            ? Math.Clamp(lv, 1, 100) : 25;

        var q = _db.Integrations.AsNoTracking().Where(i => i.IsActive);
        q = IntegrationQuery.FilterByName(q, name);
        q = IntegrationQuery.FilterByType(q, type);

        var rows = await q.OrderBy(i => i.Name).Take(limit).ToListAsync(ct);
        if (rows.Count == 0)
        {
            // An empty FILTERED result is not evidence of absence, and it has been read
            // that way in practice: a configured, health-checked NetBox reported to the
            // user as "not an integration" because the filter did not match how someone
            // capitalised the name. So when a filter was in play, answer with what does
            // exist. The next statement is then a fact rather than an inference from a
            // query the caller chose badly.
            var filtered = !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(type);
            if (!filtered)
                return JsonSerializer.SerializeToElement(
                    new { integrations = Array.Empty<object>(), count = 0 });

            const int NameCap = 50;
            // Counted separately from the list. Reporting the capped list length as the
            // total would tell the model there are exactly 50 in a payload that also
            // instructs it to treat the list as authoritative — a truncated list
            // presented as complete is the precise failure this hint exists to prevent.
            var total = await _db.Integrations.CountAsync(i => i.IsActive, ct);
            var names = await _db.Integrations.AsNoTracking()
                .Where(i => i.IsActive).OrderBy(i => i.Name)
                .Select(i => i.Name).Take(NameCap).ToListAsync(ct);

            return JsonSerializer.SerializeToElement(new
            {
                integrations = Array.Empty<object>(),
                count = 0,
                total_without_filter = total,
                integration_names = names,
                names_truncated = total > names.Count,
                note = total > 0
                    ? $"No integration matched that filter, but {total} is/are configured"
                      + (total > names.Count
                          ? $" — integration_names lists the first {names.Count}. Call again without a "
                            + "filter to see the rest."
                          : " — see integration_names.")
                      + " Do not report a system as unconfigured on the strength of a filtered lookup."
                    : "No integrations are configured at all.",
            });
        }

        var ids = rows.Select(r => r.IntegrationId).ToList();

        Dictionary<Guid, List<object>> actionsById = new();
        if (includeActions)
        {
            var aq = _db.IntegrationActions.AsNoTracking()
                .Where(a => a.IsActive && a.Enabled && ids.Contains(a.IntegrationId));
            if (!string.IsNullOrWhiteSpace(actionKeyword))
            {
                var pattern = $"%{actionKeyword}%";
                aq = aq.Where(a =>
                    EF.Functions.ILike(a.Name, pattern) || EF.Functions.ILike(a.Path, pattern));
            }

            var actions = await aq.OrderBy(a => a.Category).ThenBy(a => a.Path).ToListAsync(ct);
            actionsById = actions
                .GroupBy(a => a.IntegrationId)
                // Capped per integration: a full NetBox spec is hundreds of
                // operations and would swamp the model's context in one call.
                .ToDictionary(g => g.Key, g => g.Take(MaxActionsPerIntegration).Select(a => (object)new
                {
                    operation_id = a.OperationId,
                    name = a.Name,
                    method = a.Method,
                    path = a.Path,
                    category = a.Category,
                    read_only = a.ReadOnly,
                }).ToList());
        }

        // How many the filter actually matched, independent of `limit`. Reporting only
        // `count` (the page size) has the same failure mode the empty branch was fixed
        // for: the model treats a truncated list as the complete set. Substring name
        // matching makes over-limit results ordinary — `name: "net"` now matches every
        // NetBox-ish row where exact matching returned at most one.
        //
        // A short page cannot have been truncated, so the extra round trip is only
        // taken when the answer could actually differ.
        var matched = rows.Count < limit ? rows.Count : await q.CountAsync(ct);

        var result = rows.Select(i => new
        {
            name = i.Name,
            slug = i.Slug,
            type = i.Type,
            description = i.Description,
            base_url = i.BaseUrl,
            status = i.Status,
            enabled = i.Enabled,
            // Same definition as GET /api/integrations: a linked credential counts, and
            // an auth_config that resolves to `none` does not. The agent reasons about
            // "can I call this?" from here, so a laxer answer sends it down a path that
            // ends in an unexplained 401.
            has_credentials = i.AuthCredentialId is not null || AuthMethodOf(i) != IntegrationAuthConfig.MethodNone,
            last_checked_at = i.LastCheckedAt,
            actions = includeActions ? actionsById.GetValueOrDefault(i.IntegrationId, []) : null,
        });

        var truncated = matched > rows.Count;
        return JsonSerializer.SerializeToElement(new
        {
            integrations = result,
            count = rows.Count,
            total_matched = matched,
            truncated,
            // Spelled out rather than left as a bare boolean: the failure this guards
            // against is the model reading a capped list as the complete set, and it
            // will not infer that from a flag nothing told it about.
            note = truncated
                ? $"Showing {rows.Count} of {matched} matches. Raise `limit` or narrow the "
                  + "filter before drawing any conclusion about what is or is not configured."
                : null,
        });
    }

    // A stored config that will not parse must not read as "authenticated"; the
    // controller degrades the same way rather than failing the whole listing.
    private static string AuthMethodOf(Data.Models.Integration i)
    {
        try { return IntegrationAuthConfig.TryParse(i.AuthConfig)?.ResolvedMethod() ?? IntegrationAuthConfig.MethodNone; }
        catch (Exceptions.ValidationException) { return IntegrationAuthConfig.MethodNone; }
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement a, string k, bool def) =>
        a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : def;
}
