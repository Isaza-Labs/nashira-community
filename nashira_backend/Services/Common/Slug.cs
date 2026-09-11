using System.Globalization;
using System.Text;

namespace nashira_backend.Services.Common;

// Stable, URL-safe identifiers derived from a display name.
//
// A slug is assigned once at creation and never recomputed, because it is the name
// an exported bundle uses to refer to a row on another instance — recomputing it on
// rename would break every bundle already shared.
public static class Slug
{
    public static string From(string name, int maxLength = 64)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        // Decompose so accented letters keep their base form ("Añejo" -> "anejo")
        // instead of collapsing to a hyphen.
        var normalized = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > maxLength) slug = slug[..maxLength].TrimEnd('-');
        return slug;
    }

    // Appends -2, -3, … until `isTaken` says the slug is free. Used at creation so
    // two integrations named "NetBox (lab)" and "NetBox lab" cannot collide.
    public static string Unique(string name, Func<string, bool> isTaken, int maxLength = 64)
    {
        var baseSlug = From(name, maxLength);
        if (baseSlug.Length == 0) baseSlug = "item";
        if (!isTaken(baseSlug)) return baseSlug;

        for (var n = 2; n < 1000; n++)
        {
            var suffix = $"-{n}";
            var trimmed = baseSlug.Length + suffix.Length > maxLength
                ? baseSlug[..(maxLength - suffix.Length)].TrimEnd('-')
                : baseSlug;
            var candidate = trimmed + suffix;
            if (!isTaken(candidate)) return candidate;
        }

        // 998 collisions on one name is not a real scenario; falling back to a
        // guid fragment keeps it a bad name rather than a failed create.
        return $"{baseSlug}-{Guid.NewGuid():N}"[..maxLength];
    }
}
