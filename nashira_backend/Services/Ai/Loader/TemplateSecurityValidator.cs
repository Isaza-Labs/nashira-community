using System.Text;
using System.Text.RegularExpressions;
using nashira_backend.Services.Ai.Specs;

namespace nashira_backend.Services.Ai.Loader;

public sealed record TemplateIssue(string Severity, string Message);

public sealed record TemplateValidationResult(bool Ok, IReadOnlyList<TemplateIssue> Issues)
{
    public static TemplateValidationResult From(List<TemplateIssue> issues) =>
        new(!issues.Any(i => i.Severity == TemplateSecurityValidator.Error), issues);
}

// Validates community-authored skill (.md) and spec (.yaml) uploads before they are
// persisted + hot-reloaded. Skills are prompts, so the checks target prompt-override /
// secret-leak / governance-bypass attempts; specs must parse as OpenAPI and stay within
// size/operation bounds. Ok = no error-severity issues (warnings are allowed).
public interface ITemplateSecurityValidator
{
    TemplateValidationResult ValidateSkill(string name, string content);
    TemplateValidationResult ValidateSpec(string api, string content);
}

public sealed class TemplateSecurityValidator : ITemplateSecurityValidator
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const int MaxSkillBytes = 64 * 1024;
    public const int MaxSpecBytes = 512 * 1024;
    public const int MaxSpecOperations = 500;

    private static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (Regex Pattern, string Severity, string Message)[] SkillRules =
    [
        (R(@"ignore\s+(all\s+)?(previous|prior|above)\s+instructions"), Error, "attempts to override prior instructions"),
        (R(@"disregard\s+(the\s+)?(system|above|previous|prior)"), Error, "attempts to disregard system/prior instructions"),
        (R(@"(reveal|print|show|dump|exfiltrate).{0,40}(system\s+prompt|secret|token|password|credential|api[_\s-]?key)"), Error, "attempts to leak secrets or the system prompt"),
        (R(@"(bypass|disable|skip|ignore).{0,25}(confirmation|approval|permission|governance|safety|guard)"), Error, "attempts to bypass permission/governance controls"),
        (R(@"<script|javascript:|eval\s*\("), Warning, "contains script/exec markers"),
    ];

    private static readonly Regex MetadataUrl = R(@"169\.254\.169\.254|\blocalhost\b|127\.0\.0\.1");

    public TemplateValidationResult ValidateSkill(string name, string content)
    {
        var issues = new List<TemplateIssue>();
        if (string.IsNullOrWhiteSpace(content))
        {
            issues.Add(new TemplateIssue(Error, "skill content is empty"));
            return TemplateValidationResult.From(issues);
        }
        if (Encoding.UTF8.GetByteCount(content) > MaxSkillBytes)
            issues.Add(new TemplateIssue(Error, $"skill exceeds the {MaxSkillBytes / 1024} KiB limit"));

        foreach (var (pattern, severity, message) in SkillRules)
            if (pattern.IsMatch(content))
                issues.Add(new TemplateIssue(severity, message));

        return TemplateValidationResult.From(issues);
    }

    public TemplateValidationResult ValidateSpec(string api, string content)
    {
        var issues = new List<TemplateIssue>();
        if (string.IsNullOrWhiteSpace(content))
        {
            issues.Add(new TemplateIssue(Error, "spec content is empty"));
            return TemplateValidationResult.From(issues);
        }
        if (Encoding.UTF8.GetByteCount(content) > MaxSpecBytes)
            issues.Add(new TemplateIssue(Error, $"spec exceeds the {MaxSpecBytes / 1024} KiB limit"));

        int operations;
        try
        {
            operations = YamlSpecIndex.ParseOperations(api, content).Count();
        }
        catch (Exception ex)
        {
            issues.Add(new TemplateIssue(Error, $"not valid OpenAPI YAML: {ex.Message}"));
            return TemplateValidationResult.From(issues);
        }

        if (operations == 0)
            issues.Add(new TemplateIssue(Warning, "no operations found in the spec"));
        if (operations > MaxSpecOperations)
            issues.Add(new TemplateIssue(Error, $"spec has {operations} operations (limit {MaxSpecOperations})"));
        if (MetadataUrl.IsMatch(content))
            issues.Add(new TemplateIssue(Warning, "spec references a loopback/metadata address (possible SSRF)"));

        return TemplateValidationResult.From(issues);
    }
}
