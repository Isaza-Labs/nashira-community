using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Configuration.Modules;
using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Lists the tenant's OpenAPI specs (metadata only, never the YAML body). Read → autonomous.
public sealed class ListSpecsHandler : IToolHandler
{
    private static readonly JsonElement Schema =
        JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""").RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ModuleSelection _modules;
    private readonly IApiSpecIndex _index;

    public ListSpecsHandler(
        AppDbContext db, ICurrentUser user, ModuleSelection modules, IApiSpecIndex index)
    {
        _db = db;
        _user = user;
        _modules = modules;
        _index = index;
    }

    public string Name => "list_specs";
    public string Description =>
        "Lists the OpenAPI specs available in this deployment (metadata only: id, api name, operation " +
        "count, base_url, auth_type). Does NOT return the spec body — use get_spec for that. Use the " +
        "ai_api_spec_id (as spec_id) or api name with get_spec / update_spec / delete_spec.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        await _index.EnsureLoadedAsync(ct);
        var operationCounts = _index.All()
            .GroupBy(operation => operation.Api, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var stored = await _db.AiApiSpecs.AsNoTracking()
            .Where(spec => spec.IsActive)
            .OrderBy(spec => spec.Api)
            .ToListAsync(ct);
        var rows = stored
            .Where(spec => ModuleContentCatalog.IsSpecAvailable(spec.Api, _modules))
            .Select(spec => new
            {
                ai_api_spec_id = spec.AiApiSpecId,
                api = spec.Api,
                operation_count = operationCounts.GetValueOrDefault(spec.Api),
                base_url = spec.BaseUrl,
                auth_type = spec.AuthType,
                verify_ssl = spec.VerifySsl,
                created_at = spec.CreatedAt,
                updated_at = spec.UpdatedAt,
            })
            .ToList();
        return JsonSerializer.SerializeToElement(new { specs = rows, count = rows.Count });
    }
}
