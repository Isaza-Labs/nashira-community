using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Searches operations across the tenant's specs. Returns compact summaries; with
// include_details=true (and a narrow result set, <=5) it inlines parameter +
// request body schemas so the "discover -> detail -> execute" triplet collapses
// into one call.
public sealed class DiscoverOperationsHandler : IToolHandler
{
    private const int InlineDetailCap = 5;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "keyword":{"type":"string","description":"Substring match on operation_id, summary, path, tags"},
          "api":{"type":"string","description":"Limit to one API (use list_apis to enumerate)"},
          "method":{"type":"string","description":"HTTP verb filter: GET/POST/PUT/PATCH/DELETE"},
          "limit":{"type":"integer","minimum":1,"maximum":100,"default":25},
          "include_details":{"type":"boolean","default":false,"description":"Inline parameter + request body schemas when the result set is small (<=5); skips the operation_detail round-trip."}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IApiSpecIndex _index;
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public DiscoverOperationsHandler(IApiSpecIndex index, AppDbContext db, ICurrentUser user)
    {
        _index = index;
        _db = db;
        _user = user;
    }

    public string Name => "discover_operations";
    public string Description =>
        "Searches API operations by keyword, api, or method. Returns compact summaries — " +
        "call operation_detail for the full schema before execute_operation. Prefer filtering " +
        "by `api` when the target is known; keyword is OR-matched against operation_id, summary, " +
        "path, and tags. Pass include_details=true on a narrow search (<=5 results) to inline " +
        "schemas and skip the operation_detail round-trip.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        await _index.EnsureLoadedAsync(ct);

        var keyword = GetString(args, "keyword") ?? string.Empty;
        var api = GetString(args, "api");
        var method = GetString(args, "method");
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? lv : 25;
        var includeDetails = args.TryGetProperty("include_details", out var idEl) && idEl.ValueKind == JsonValueKind.True;

        var matches = _index.Search(keyword, api, method)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();

        if (!includeDetails || matches.Count > InlineDetailCap)
        {
            var compact = matches.Select(o => new
            {
                operation_id = o.OperationId,
                api = o.Api,
                method = o.Method,
                path = o.Path,
                summary = o.Summary,
                tags = o.Tags,
            }).ToList();

            return JsonSerializer.SerializeToElement(new
            {
                count = compact.Count,
                operations = compact,
                details_inlined = false,
                details_skipped_reason = includeDetails && matches.Count > InlineDetailCap
                    ? $"result set ({matches.Count}) exceeds inline cap ({InlineDetailCap}); narrow the search and retry"
                    : null,
            });
        }

        var apis = matches.Select(o => o.Api).Distinct().ToList();
        var yamls = await _db.AiApiSpecs.AsNoTracking()
            .Where(s => s.IsActive && apis.Contains(s.Api))
            .ToDictionaryAsync(s => s.Api, s => s.Content, ct);

        var expanded = matches.Select(o =>
        {
            OperationYamlSlicer.DetailSlice? slice = null;
            if (yamls.TryGetValue(o.Api, out var yaml) && !string.IsNullOrEmpty(yaml))
            {
                try { slice = OperationYamlSlicer.ExtractDetail(yaml, o.Method, o.Path); }
                catch { slice = null; }
            }
            return new
            {
                operation_id = o.OperationId,
                api = o.Api,
                method = o.Method,
                path = o.Path,
                summary = o.Summary,
                tags = o.Tags,
                parameters = slice?.Parameters,
                request_body = slice?.RequestBody,
                response_preview = slice?.ResponsePreview,
            };
        }).ToList();

        return JsonSerializer.SerializeToElement(new { count = expanded.Count, operations = expanded, details_inlined = true });
    }

    private static string? GetString(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
