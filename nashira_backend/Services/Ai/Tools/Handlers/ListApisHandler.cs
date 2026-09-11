using System.Text.Json;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Lists the REST APIs available to the tenant. First step of the
// discover -> detail -> execute flow.
public sealed class ListApisHandler : IToolHandler
{
    private readonly IApiSpecIndex _index;
    private readonly ICurrentUser _user;

    public ListApisHandler(IApiSpecIndex index, ICurrentUser user)
    {
        _index = index;
        _user = user;
    }

    public string Name => "list_apis";
    public string Description =>
        "Lists the REST APIs available to this tenant. Each entry has an `api` id " +
        "(use it as the `api` argument in discover_operations), an operation count, " +
        "and its tags. This covers ONLY registered OpenAPI specs — an external system " +
        "may instead be reachable as an MCP server (list_mcp_tools) or be registered " +
        "as an integration (list_integrations), so an empty result here is not evidence " +
        "that the system is unavailable.";
    public JsonElement ParametersSchema => ToolSchemas.Empty;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        await _index.EnsureLoadedAsync(ct);
        var grouped = _index.All()
            .GroupBy(o => o.Api, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                api = g.Key,
                operation_count = g.Count(),
                tags = g.SelectMany(o => o.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToList(),
            })
            .OrderBy(x => x.api)
            .ToList();
        return JsonSerializer.SerializeToElement(new { apis = grouped });
    }
}
