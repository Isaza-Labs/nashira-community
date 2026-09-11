using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Loader;
using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Identity;
using SpecEntity = nashira_backend.Data.Models.AiApiSpec;
using ValidationRecordEntity = nashira_backend.Data.Models.ValidationRecord;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Registers a new OpenAPI spec (the agent's discover/detail/execute catalog). The YAML is
// security-validated and reparsed for its operation count, then the index is reloaded.
// Admin-only; write → single_confirm.
public sealed class CreateSpecHandler : IToolHandler
{
    internal static readonly string[] AllowedAuthTypes = ["none", "token", "bearer", "basic", "header"];

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "api":{"type":"string","description":"Identifier the agent uses (e.g. \"netbox\"); lowercased, unique per tenant"},
          "content":{"type":"string","description":"Raw OpenAPI 3.x YAML"},
          "base_url":{"type":"string","description":"Absolute base URL used when executing operations"},
          "auth_type":{"type":"string","enum":["none","token","bearer","basic","header"],"description":"Auth scheme (default none)"},
          "auth_config":{"type":"string","description":"JSON auth config; values may hold ${secret:secret:<name>:value} refs"},
          "verify_ssl":{"type":"boolean","default":true}
        },"required":["api","content"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IApiSpecIndex _index;
    private readonly ITemplateSecurityValidator _validator;
    private readonly IValidationRecorder _recorder;

    public CreateSpecHandler(
        AppDbContext db, ICurrentUser user, IApiSpecIndex index,
        ITemplateSecurityValidator validator, IValidationRecorder recorder)
    {
        _db = db;
        _user = user;
        _index = index;
        _validator = validator;
        _recorder = recorder;
    }

    public string Name => "create_spec";
    public string Description =>
        "Registers a new OpenAPI spec so the agent can discover and execute its operations. api is a " +
        "unique lowercase id; content is raw OpenAPI 3.x YAML (security-validated before saving). " +
        "Returns the new ai_api_spec_id. Admin only.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var apiRaw = Str(args, "api");
        if (string.IsNullOrWhiteSpace(apiRaw)) return Err("api is required");
        var content = Str(args, "content");
        if (string.IsNullOrWhiteSpace(content)) return Err("content is required");
        var api = apiRaw.Trim().ToLowerInvariant();
        if (ModuleContentCatalog.IsBuiltinSpec(api))
            return Err($"api name '{api}' is reserved for a built-in spec");
        if (!TryNormalizeAuthType(Str(args, "auth_type"), out var authType))
            return Err($"auth_type must be one of: {string.Join(", ", AllowedAuthTypes)}");

        if (await ValidateAndRecordAsync(api, content, ct) is { } validationError) return validationError;

        if (await _db.AiApiSpecs.AnyAsync(s => s.Api == api && s.IsActive, ct))
            return Err("a spec with this api name already exists");

        int count;
        try { count = YamlSpecIndex.ParseOperations(api, content).Count(); }
        catch (Exception ex) { return Err($"content is not valid OpenAPI YAML: {ex.Message}"); }

        var now = DateTime.UtcNow;
        var row = new SpecEntity
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = api,
            Content = content,
            OperationCount = count,
            BaseUrl = Str(args, "base_url"),
            AuthType = authType,
            AuthConfig = Str(args, "auth_config"),
            VerifySsl = Bool(args, "verify_ssl") ?? true,
            CreatedBy = _user.UserId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AiApiSpecs.Add(row);
        await _db.SaveChangesAsync(ct);
        await _index.ReloadAsync(ct);
        return JsonSerializer.SerializeToElement(new { ai_api_spec_id = row.AiApiSpecId, api = row.Api });
    }

    // Security-validate the spec content and record the outcome; returns an Err element on failure.
    // Shared shape with update_spec.
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

    // Shared with update_spec. Mirrors the controller's NormalizeAuthType (null => "none").
    internal static bool TryNormalizeAuthType(string? raw, out string value)
    {
        value = (raw ?? "none").Trim().ToLowerInvariant();
        return AllowedAuthTypes.Contains(value);
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
