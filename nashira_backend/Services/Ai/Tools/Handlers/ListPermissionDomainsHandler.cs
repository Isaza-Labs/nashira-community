using System.Text.Json;
using nashira_backend.Services.Ai.Permissions;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Lists what a permission can be granted on. Admin-only; read → autonomous.
public sealed class ListPermissionDomainsHandler : IToolHandler
{
    private static readonly JsonElement Schema =
        JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""").RootElement.Clone();

    private readonly IPermissionCatalog _catalog;

    public ListPermissionDomainsHandler(IPermissionCatalog catalog) => _catalog = catalog;

    public string Name => "list_permission_domains";
    public string Description =>
        "Lists everything a per-user permission can be granted on: the capability domains " +
        "(device, mcp, api, workflow, …) and every registered integration, MCP server and API " +
        "spec, keyed as integration:<slug>, mcp:<server> and api:<api>. Use a key from here as " +
        "the tool_domain in set_user_permissions. Admin only.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var resources = await _catalog.ListAsync(ct);
        return JsonSerializer.SerializeToElement(new
        {
            // Grouped the way the answer is used: "what domains are there" and "which
            // of my systems can I name" are different questions.
            domains = resources.Where(r => r.Kind == PermissionCatalog.KindDomain)
                .Select(r => r.Key).ToList(),
            resources = resources.Where(r => r.Kind != PermissionCatalog.KindDomain)
                .Select(r => new { key = r.Key, kind = r.Kind, label = r.Label, description = r.Description })
                .ToList(),
        });
    }
}
