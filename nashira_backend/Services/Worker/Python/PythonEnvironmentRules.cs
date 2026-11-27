using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using nashira_backend.Exceptions;

namespace nashira_backend.Services.Worker.Python;

// The pure rules of a PythonEnvironment row: what a name may be, what a requirements
// file may contain, how credentials in it are hidden, and the hash a build is keyed by.
// Static and side-effect free so the API, the builder and the tests agree by
// construction rather than by copy.
public static partial class PythonEnvironmentRules
{
    // A slug, and also a directory name under <PackagesDir>/envs — which is why no dot,
    // slash or leading dash can get through: the name is joined into a path.
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,48}[a-z0-9]$")]
    public static partial Regex NameRegex();

    // What `uv venv --python` is handed: a version ("3.12", "3.11.9") optionally prefixed
    // by an implementation ("cpython3.12", "cpython@3.12", "pypy3.10"). Deliberately not a
    // path — a path would let the field pick an arbitrary binary on the host.
    [GeneratedRegex(@"^(?:[a-z]+[@-]?)?\d+(?:\.\d+){0,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex PythonVersionRegex();

    // scheme://userinfo@ — the userinfo part is what gets masked. `[^\s/@]` keeps the
    // match inside one URL authority, so `a@b` in a comment or an `@` in a path is left
    // alone.
    [GeneratedRegex(@"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*://)(?<userinfo>[^\s/@]+)@(?<host>[^\s/]*)")]
    private static partial Regex UrlCredentialRegex();

    public const string MaskedUserInfo = "***";

    public const int MaxRequirementsLength = 20_000;
    public const int MaxDescriptionLength = 2_000;

    // Lines that would make uv read ANOTHER file on the server. The requirements text is
    // the whole definition of the environment; letting it include a path on the host
    // would make the build depend on — and disclose the existence of — files nobody
    // reviewing this row can see.
    private static readonly string[] ForbiddenOptions =
    [
        "-r", "--requirement", "-c", "--constraint", "-e", "--editable",
    ];

    public static string RequireName(string? raw)
    {
        var name = (raw ?? string.Empty).Trim();
        if (name.Length == 0) throw new ValidationException("name is required", "python_env_name_invalid");
        if (!NameRegex().IsMatch(name))
            throw new ValidationException(
                "name must be 3–50 characters of lowercase letters, digits and dashes, starting and "
                + "ending with a letter or digit (for example `ssh-legacy`)",
                "python_env_name_invalid");
        return name;
    }

    public static bool IsValidName(string? name) => name is not null && NameRegex().IsMatch(name);

    // Null for "the image's python3". Blank counts as null so a cleared form field means
    // what it looks like it means.
    public static string? NormalizePythonVersion(string? raw)
    {
        var v = (raw ?? string.Empty).Trim();
        if (v.Length == 0) return null;
        if (v.Length > 32 || !PythonVersionRegex().IsMatch(v))
            throw new ValidationException(
                "python_version must be a version such as `3.12` or `cpython@3.11.9`, or empty for the "
                + "image's python3",
                "python_env_version_invalid");
        return v;
    }

    public static string NormalizeRequirements(string? raw)
    {
        var text = (raw ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length == 0)
            throw new ValidationException("requirements must list at least one package", "python_env_requirements_invalid");
        if (text.Length > MaxRequirementsLength)
            throw new ValidationException(
                $"requirements are limited to {MaxRequirementsLength} characters", "python_env_requirements_invalid");
        if (text.Contains('\0'))
            throw new ValidationException("requirements contain a NUL character", "python_env_requirements_invalid");

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var option = line.Split([' ', '\t', '='], 2)[0];
            if (ForbiddenOptions.Any(o => string.Equals(o, option, StringComparison.Ordinal))
                || ForbiddenOptions.Any(o => o.Length == 2 && line.StartsWith(o, StringComparison.Ordinal)
                                             && line.Length > 2 && line[2] != '-'))
                throw new ValidationException(
                    $"`{option}` is not allowed in an environment's requirements — it would read another file "
                    + "on the server. List the packages here instead.",
                    "python_env_requirements_invalid");
        }
        return text;
    }

    public static string? NormalizeDescription(string? raw)
    {
        var d = raw?.Trim();
        if (string.IsNullOrEmpty(d)) return null;
        return d.Length <= MaxDescriptionLength ? d : d[..MaxDescriptionLength];
    }

    /// <summary>
    /// Replaces the <c>user:pass@</c> part of every URL with <c>***@</c>.
    /// </summary>
    /// <remarks>
    /// A private index is usually written with its credentials inline
    /// (<c>--extra-index-url https://user:token@pypi.example/simple</c>). The row has to keep
    /// them to build, but nothing that READS the row needs them: responses, audit entries
    /// and the build error (uv echoes the URL it failed to reach) all go through here.
    /// </remarks>
    public static string? MaskCredentials(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return UrlCredentialRegex().Replace(text, m =>
            $"{m.Groups["scheme"].Value}{MaskedUserInfo}@{m.Groups["host"].Value}");
    }

    /// <summary>
    /// Puts the stored credentials back into requirements an editor submitted masked.
    /// </summary>
    /// <remarks>
    /// The edit form is filled from a masked response, so saving it unchanged sends
    /// <c>https://***@host</c> back. Without this, every edit of an environment with a
    /// private index would silently replace its credentials with three asterisks. A masked
    /// URL is matched to the stored one by scheme and host; a URL whose credentials the
    /// editor actually retyped is not masked and passes through as written.
    /// </remarks>
    public static string RestoreMaskedCredentials(string submitted, string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !submitted.Contains(MaskedUserInfo + "@", StringComparison.Ordinal))
            return submitted;

        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in UrlCredentialRegex().Matches(stored))
        {
            var masked = $"{m.Groups["scheme"].Value}{MaskedUserInfo}@{m.Groups["host"].Value}";
            originals.TryAdd(masked, m.Value);
        }

        return UrlCredentialRegex().Replace(submitted, m =>
            m.Groups["userinfo"].Value == MaskedUserInfo && originals.TryGetValue(m.Value, out var original)
                ? original
                : m.Value);
    }

    // The comment line that separates an environment's own requirements from what the
    // Python packages allowlist adds to them in the effective requirements.
    public const string IncludedHeader = "# Included from the Python packages allowlist";

    /// <summary>
    /// The pip specs allowlist rows contribute to an environment, cleaned up:
    /// trimmed, blanks dropped, deduplicated and sorted.
    /// </summary>
    /// <remarks>
    /// Sorted so the effective requirements — and therefore the build hash — do not
    /// depend on the order a query happened to return the rows in. Deduplicated on
    /// <see cref="RequirementKey"/>, so <c>Netmiko==4.3.0</c> and <c>netmiko==4.3.0</c> are
    /// one line.
    /// </remarks>
    public static IReadOnlyList<string> NormalizeIncluded(IEnumerable<string?> specs) =>
        specs
            .Select(s => s?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .DistinctBy(RequirementKey)
            .OrderBy(RequirementKey, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// What a build of an environment installs: its own <c>Requirements</c>, then every
    /// spec the allowlist contributes that the environment does not already list.
    /// </summary>
    /// <remarks>
    /// With nothing to add the result is the environment's own requirements exactly, so
    /// an environment no allowlist row targets keeps the hash it was built with before
    /// install targets existed. A contributed spec that only overlaps an own line by
    /// package name (<c>netmiko&gt;=4,&lt;5</c> here, <c>netmiko==4.3.0</c> from the allowlist)
    /// is kept: uv intersects the two, and a contradiction fails the build visibly
    /// instead of one side silently winning.
    /// </remarks>
    public static string EffectiveRequirements(string? own, IEnumerable<string?> included)
    {
        var ownText = (own ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var ownKeys = ownText.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(RequirementKey)
            .ToHashSet(StringComparer.Ordinal);

        var extra = NormalizeIncluded(included).Where(s => !ownKeys.Contains(RequirementKey(s))).ToList();
        if (extra.Count == 0) return ownText;

        var sb = new StringBuilder(ownText);
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(IncludedHeader);
        foreach (var spec in extra) sb.Append('\n').Append(spec);
        return sb.ToString();
    }

    // The hash of a build of the effective requirements — what RequirementsHash holds.
    public static string EffectiveHash(string? pythonVersion, string? own, IEnumerable<string?> included) =>
        Hash(pythonVersion, EffectiveRequirements(own, included));

    // Two requirement lines are the same line when they differ only in case and
    // whitespace: `netmiko == 4.3.0` and `Netmiko==4.3.0` ask pip for the same thing.
    private static string RequirementKey(string line) =>
        new string(line.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    // What a build is keyed by. Line endings are normalised first, so the same file
    // saved from Windows and from Linux is the same environment.
    public static string Hash(string? pythonVersion, string requirements)
    {
        var canonical = $"python={pythonVersion ?? string.Empty}\n{requirements.Replace("\r\n", "\n").Trim()}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // Build output is long and the useful part is at the end.
    public static string Tail(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        s = s.Trim();
        return s.Length <= max ? s : "…" + s[^max..];
    }
}
