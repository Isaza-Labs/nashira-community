using System.Text.RegularExpressions;
using nashira_backend.Services.Ai.Loader;
using nashira_backend.Services.Ai.Specs;

namespace nashira_backend.Tests;

// The shipped Skills/*.md are the agent's system prompt on every turn, and they are
// prose about an API rather than code that calls one — so nothing fails when they drift.
// A skill that names an endpoint the catalog does not carry does not error: the agent
// reads the instruction, cannot find the operation, and either invents one or tells the
// user the capability does not exist. That is what happened with /api/device-pools,
// which the inventory skill sent the agent to through execute_operation for a release
// while no shipped spec described it.
//
// These tests are the compiler this directory does not otherwise have.
public class BuiltinSkillTests
{
    private static readonly Regex ApiPath = new(
        @"/api/[A-Za-z0-9/_{}.\-]*[A-Za-z0-9}]", RegexOptions.Compiled);

    // `na_workflows`, `na_snippets`, … as written inside backticks or plain prose.
    private static readonly Regex SpecName = new(@"\bna_[a-z_]+\b", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nashira.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IReadOnlyList<(string Name, string Text)> Skills()
    {
        var dir = Path.Combine(RepoRoot(), "nashira_backend", "Skills");
        Assert.True(Directory.Exists(dir), $"Skills directory not found at {dir}");
        var files = Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories)
            // Skills/vendors is seed data for VendorCommandSeeder, not prompt text.
            // SkillPromptLoader skips it for the same reason, so holding its README to
            // the skill rules would only produce failures about a file no prompt sees.
            .Where(f => Path.GetRelativePath(dir, f)
                .Split(Path.DirectorySeparatorChar)[0] != "vendors")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();
        Assert.NotEmpty(files);
        return files;
    }

    private static IReadOnlyList<string> SpecFiles() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "nashira_backend", "Specs"), "*.yaml")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    // Path templates differ between the spec and the prose that quotes it — the spec
    // says {workflowId} where a skill says {id} — and neither is wrong, so compare the
    // shape rather than the parameter names.
    private static string Normalize(string path) =>
        Regex.Replace(path, @"\{[^}]*\}", "{}");

    // Editing a built-in skill goes through PUT /api/skills/builtin/{name}, which runs
    // this same validator. A shipped file that trips an error rule cannot be saved back:
    // the admin editor would load the file and then refuse to store it unchanged. It is
    // an easy rule to break by accident, because the skills describe the rules.
    [Fact]
    public void Every_builtin_skill_passes_the_security_validator()
    {
        var validator = new TemplateSecurityValidator();
        foreach (var (name, text) in Skills())
        {
            var result = validator.ValidateSkill(name, text);
            var errors = result.Issues
                .Where(i => i.Severity == TemplateSecurityValidator.Error)
                .Select(i => i.Message);
            Assert.True(result.Ok, $"{name}: {string.Join("; ", errors)}");
        }
    }

    [Fact]
    public void Every_spec_a_skill_names_is_actually_shipped()
    {
        var shipped = SpecFiles()
            .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var references = 0;
        foreach (var (name, text) in Skills())
        {
            foreach (Match m in SpecName.Matches(text))
            {
                Assert.True(
                    shipped.Contains(m.Value),
                    $"{name} names spec '{m.Value}', which is not in Specs/");
                references++;
            }
        }

        // Guard against the test passing because the regex stopped matching anything.
        Assert.True(references > 10, $"only {references} spec references found across the skills");
    }

    // The one that would have caught /api/device-pools. A skill telling the agent to
    // reach an endpoint through execute_operation is only true if some shipped spec
    // describes it — that is the agent's entire map of the API.
    [Fact]
    public void Every_api_path_a_skill_names_is_described_by_a_shipped_spec()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SpecFiles())
        {
            var api = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            foreach (var op in YamlSpecIndex.ParseOperations(api, File.ReadAllText(file)))
                known.Add(Normalize(op.Path));
        }
        Assert.NotEmpty(known);

        var quotedPaths = 0;
        foreach (var (name, text) in Skills())
        {
            foreach (Match m in ApiPath.Matches(text))
            {
                var quoted = Normalize(m.Value);

                // A skill routinely names a prefix rather than a whole path — "manage it
                // under /api/reports" — so a prefix of a real path counts as described.
                var resolves = known.Contains(quoted)
                    || known.Any(k => k.StartsWith(quoted.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));

                Assert.True(resolves,
                    $"{name} names '{m.Value}', which no shipped spec describes — the agent "
                    + "has no execute_operation route to it.");
                quotedPaths++;
            }
        }

        // Same guard: a regex that matches nothing would make this test green forever.
        Assert.True(quotedPaths > 10, $"only {quotedPaths} API paths found across the skills");
    }
}
