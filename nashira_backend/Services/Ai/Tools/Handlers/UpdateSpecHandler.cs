using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Loader;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Identity;
using SpecEntity = nashira_backend.Data.Models.AiApiSpec;
using ValidationRecordEntity = nashira_backend.Data.Models.ValidationRecord;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Updates an OpenAPI spec (resolved by id or api name). Only provided fields change; a new
// content body is re-validated and its operation count recomputed, then the index is reloaded.
// Admin-only; write → single_confirm.
public sealed class UpdateSpecHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "spec_id":{"type":"string","description":"Spec id (ai_api_spec_id) — or identify by api name"},
          "api":{"type":"string","description":"Spec api name if spec_id is omitted"},
          "content":{"type":"string","description":"Replacement OpenAPI 3.x YAML"},
          "base_url":{"type":"string"},
          "auth_type":{"type":"string","enum":["none","token","bearer","basic","header"]},
          "auth_config":{"type":"string","description":"JSON auth config; values may hold ${secret:secret:<name>:value} refs"},
          "verify_ssl":{"type":"boolean"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IApiSpecIndex _index;
    private readonly ITemplateSecurityValidator _validator;
    private readonly IValidationRecorder _recorder;

    public UpdateSpecHandler(
        AppDbContext db, ICurrentUser user, IApiSpecIndex index,
        ITemplateSecurityValidator validator, IValidationRecorder recorder)
    {
        _db = db;
        _user = user;
        _index = index;
        _validator = validator;
        _recorder = recorder;
    }

    public string Name => "update_spec";
    public string Description =>
        "Updates an OpenAPI spec identified by spec_id or api name. Only provided fields change. A new " +
        "content body is security-validated and its operation count recomputed. Admin only.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var row = await ResolveAsync(args, ct);
        if (row is null) return Err("api spec not found (pass a valid spec_id or api)");

        if (Str(args, "content") is { } content)
        {
            if (await ValidateAndRecordAsync(row.Api, content, ct) is { } validationError) return validationError;
            int count;
            try { count = YamlSpecIndex.ParseOperations(row.Api, content).Count(); }
            catch (Exception ex) { return Err($"content is not valid OpenAPI YAML: {ex.Message}"); }
            row.Content = content;
            row.OperationCount = count;
        }
        if (Str(args, "base_url") is { } baseUrl) row.BaseUrl = baseUrl;
        if (Str(args, "auth_type") is { } rawAuth)
        {
            if (!CreateSpecHandler.TryNormalizeAuthType(rawAuth, out var authType))
                return Err($"auth_type must be one of: {string.Join(", ", CreateSpecHandler.AllowedAuthTypes)}");
            row.AuthType = authType;
        }
        if (Str(args, "auth_config") is { } authConfig) row.AuthConfig = authConfig;
        if (Bool(args, "verify_ssl") is { } verifySsl) row.VerifySsl = verifySsl;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _index.ReloadAsync(ct);
        return JsonSerializer.SerializeToElement(new { ai_api_spec_id = row.AiApiSpecId, api = row.Api, updated = true });
    }

    private async Task<SpecEntity?> ResolveAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "spec_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            return await _db.AiApiSpecs.FirstOrDefaultAsync(
                s => s.AiApiSpecId == id && s.IsActive, ct);
        if (Str(args, "api")?.Trim().ToLowerInvariant() is { Length: > 0 } api)
            return await _db.AiApiSpecs.FirstOrDefaultAsync(
                s => s.Api == api && s.IsActive, ct);
        return null;
    }

    // Security-validate the spec content and record the outcome; returns an Err element on failure.
    private async Task<JsonElement?> ValidateAndRecordAsync(string api, string content, CancellationToken ct)
    {
        var result = _validator.ValidateSpec(api, content);
        await _recorder.RecordAsync(ValidationRecordEntity.KindSpec, api, result, ct);
        if (!result.Ok)
        {
            var errors = string.Join("; ", result.Issues
                .Where(i => i.Severity == TemplateSecurityValidator.Error).Select(i => i.Message));
            return Err($"spec failed validation: {errors}");
        }
        return null;
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
