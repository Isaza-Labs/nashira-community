using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Hydrates one operation_id with parameter + request body schema so the agent
// can fill execute_operation correctly. Reparses the spec YAML on demand.
public sealed class OperationDetailHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "operation_id":{"type":"string","description":"From discover_operations"}
        },"required":["operation_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IApiSpecIndex _index;
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public OperationDetailHandler(IApiSpecIndex index, AppDbContext db, ICurrentUser user)
    {
        _index = index;
        _db = db;
        _user = user;
    }

    public string Name => "operation_detail";
    public string Description =>
        "Returns full parameter + request body schema for one operation_id. Always call this " +
        "before execute_operation on an operation you haven't called before — parameter shapes " +
        "shift between APIs. For a one/two-candidate search, prefer discover_operations(..., " +
        "include_details=true) to skip this round-trip.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("operation_id", out var opIdEl) || opIdEl.ValueKind != JsonValueKind.String)
            return Error("operation_id is required");

        var operationId = opIdEl.GetString() ?? string.Empty;
        await _index.EnsureLoadedAsync(ct);

        var meta = _index.GetByOperationId(operationId);
        if (meta is null) return Error($"operation '{operationId}' not found");

        var yaml = await _db.AiApiSpecs.AsNoTracking()
            .Where(s => s.Api == meta.Api && s.IsActive)
            .Select(s => s.Content)
            .FirstOrDefaultAsync(ct);
        if (yaml is null) return Error($"spec '{meta.Api}' not available");

        var detail = OperationYamlSlicer.ExtractDetail(yaml, meta.Method, meta.Path);

        return JsonSerializer.SerializeToElement(new
        {
            operation_id = meta.OperationId,
            api = meta.Api,
            method = meta.Method,
            path = meta.Path,
            summary = meta.Summary,
            description = meta.Description,
            tags = meta.Tags,
            parameters = detail.Parameters,
            request_body = detail.RequestBody,
            response_preview = detail.ResponsePreview,
        });
    }

    private static JsonElement Error(string msg) => JsonSerializer.SerializeToElement(new { error = msg });
}
