using System.Text.Json;
using nashira_backend.Services.Ai.Loader;
using ValidationRecordEntity = nashira_backend.Data.Models.ValidationRecord;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Dry-run security validation of a skill/spec template (no save). Admin-only; no mutation → autonomous.
public sealed class ValidateTemplateHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "kind":{"type":"string","enum":["skill","spec"],"description":"Template kind"},
          "name":{"type":"string"},
          "content":{"type":"string","description":"Template content to check"}
        },"required":["kind","content"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly ITemplateSecurityValidator _validator;

    public ValidateTemplateHandler(ITemplateSecurityValidator validator) => _validator = validator;

    public string Name => "validate_template";
    public string Description =>
        "Runs the security validator over a skill or spec template WITHOUT saving it, returning ok + any " +
        "issues (severity/message). Admin only.";
    public JsonElement ParametersSchema => Schema;

    public Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var kind = (Str(args, "kind") ?? string.Empty).Trim().ToLowerInvariant();
        var name = Str(args, "name") ?? string.Empty;
        var content = Str(args, "content") ?? string.Empty;

        if (kind != ValidationRecordEntity.KindSkill && kind != ValidationRecordEntity.KindSpec)
            return Task.FromResult(Err("kind must be 'skill' or 'spec'"));

        var result = kind == ValidationRecordEntity.KindSkill
            ? _validator.ValidateSkill(name, content)
            : _validator.ValidateSpec(name, content);

        return Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            ok = result.Ok,
            issues = result.Issues.Select(i => new { severity = i.Severity, message = i.Message }).ToList(),
        }));
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
