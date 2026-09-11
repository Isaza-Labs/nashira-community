using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Configuration.Modules;
using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using SpecEntity = nashira_backend.Data.Models.AiApiSpec;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Details of one OpenAPI spec (resolved by id or api name), including the YAML body. Read → autonomous.
public sealed class GetSpecHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "spec_id":{"type":"string","description":"Spec id (ai_api_spec_id) — or identify by api name"},
          "api":{"type":"string","description":"Spec api name (e.g. \"netbox\") if no spec_id"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ModuleSelection _modules;

    public GetSpecHandler(AppDbContext db, ICurrentUser user, ModuleSelection modules)
    {
        _db = db;
        _user = user;
        _modules = modules;
    }

    public string Name => "get_spec";
    public string Description =>
        "Returns one available OpenAPI spec (id, api, operation count, base_url, auth_type, verify_ssl, and " +
        "YAML content limited to this deployment's enabled capabilities), resolved by spec_id or api name. The content can be large — prefer list_specs when " +
        "you only need metadata.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var s = await ResolveAsync(args, ct);
        if (s is null || !ModuleContentCatalog.IsSpecAvailable(s.Api, _modules))
            return Err("api spec not found (pass a valid spec_id or api)");

        var content = YamlSpecIndex.FilterContent(s.Api, s.Content, _modules);
        var operationCount = YamlSpecIndex.ParseOperations(s.Api, content).Count();

        return JsonSerializer.SerializeToElement(new
        {
            ai_api_spec_id = s.AiApiSpecId,
            api = s.Api,
            operation_count = operationCount,
            base_url = s.BaseUrl,
            auth_type = s.AuthType,
            verify_ssl = s.VerifySsl,
            created_at = s.CreatedAt,
            updated_at = s.UpdatedAt,
            content,
        });
    }

    private async Task<SpecEntity?> ResolveAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "spec_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            return await _db.AiApiSpecs.AsNoTracking().FirstOrDefaultAsync(
                s => s.AiApiSpecId == id && s.IsActive, ct);
        if (Str(args, "api")?.Trim().ToLowerInvariant() is { Length: > 0 } api)
            return await _db.AiApiSpecs.AsNoTracking().FirstOrDefaultAsync(
                s => s.Api == api && s.IsActive, ct);
        return null;
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
