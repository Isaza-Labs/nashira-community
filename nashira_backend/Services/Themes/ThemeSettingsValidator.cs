using System.Text.Json;
using nashira_backend.Exceptions;

namespace nashira_backend.Services.Themes;

// Validation for a theme's style settings — the non-colour half of a theme
// (corner roundness, interface scale, font stacks, heading weight).
//
// Why this is validated when ColorsJson deliberately is not: a colour entry can
// only ever end up as an `oklch()` the frontend generates itself, and which
// token names exist is a frontend concern that would drag the backend into every
// design-system change. A style setting is different on both counts. The values
// are interpolated straight into `font-family` and `border-radius` declarations
// on a page that OTHER people load the moment a theme is shared, so an unchecked
// string there is a styling injection with extra steps. And the vocabulary is
// six keys that move about as often as the design system itself, so pinning it
// down costs nothing in maintenance.
//
// Everything is optional. An absent key means "inherit app.css", which is what
// lets a colours-only theme — every theme saved before this existed — keep
// behaving exactly as it did.
public static class ThemeSettingsValidator
{
    // Ranges mirror the frontend's sliders (lib/stores/theme.svelte.ts). Kept
    // deliberately narrow: interface scale is a comfort adjustment, not a zoom,
    // and a roundness above 2 turns every card into a lozenge.
    public const double RoundnessMin = 0;
    public const double RoundnessMax = 2;
    public const double UiScaleMin = 0.85;
    public const double UiScaleMax = 1.15;
    public const double HeadingWeightMin = 300;
    public const double HeadingWeightMax = 900;

    // Font choices are a closed vocabulary of stacks the frontend already knows
    // how to write. `plex` is what the app bundles (@fontsource IBM Plex); every
    // other option is system-resident. Nothing here can pull a remote webfont,
    // which is the whole point — a shared theme must not be able to make every
    // user's browser fetch a third-party asset.
    public static readonly IReadOnlySet<string> BodyFonts =
        new HashSet<string>(StringComparer.Ordinal) { "plex", "system", "serif", "mono" };
    public static readonly IReadOnlySet<string> HeadingFonts =
        new HashSet<string>(StringComparer.Ordinal) { "inherit", "plex", "system", "serif", "mono" };
    public static readonly IReadOnlySet<string> MonoFonts =
        new HashSet<string>(StringComparer.Ordinal) { "plex", "system" };

    public static readonly IReadOnlyList<string> Keys =
    [
        "font_body", "font_heading", "font_mono", "heading_weight", "roundness", "ui_scale",
    ];

    /// <summary>
    /// The canonical JSON for a settings payload, ready to store. Null or a JSON
    /// null means "no settings" and yields an empty object rather than null, so
    /// the column never has to be read defensively.
    /// </summary>
    public static string Normalize(JsonElement? raw)
    {
        if (raw is not { } settings || settings.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "{}";
        if (settings.ValueKind != JsonValueKind.Object)
            throw new ValidationException("settings must be a JSON object", code: "settings_invalid");

        // Ordinal comparer, and unknown keys are rejected rather than dropped: a
        // typo that silently vanishes leaves someone convinced they saved a
        // setting the app then ignores forever.
        var cleaned = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var prop in settings.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "roundness":
                    cleaned[prop.Name] = Number(prop, RoundnessMin, RoundnessMax);
                    break;
                case "ui_scale":
                    cleaned[prop.Name] = Number(prop, UiScaleMin, UiScaleMax);
                    break;
                case "heading_weight":
                    // Stored as an int: it lands in a CSS font-weight, where 650.5
                    // is meaningless, and rounding here keeps the value the editor
                    // reads back identical to the one it sent.
                    cleaned[prop.Name] = (int)Math.Round(Number(prop, HeadingWeightMin, HeadingWeightMax));
                    break;
                case "font_body":
                    cleaned[prop.Name] = Choice(prop, BodyFonts);
                    break;
                case "font_heading":
                    cleaned[prop.Name] = Choice(prop, HeadingFonts);
                    break;
                case "font_mono":
                    cleaned[prop.Name] = Choice(prop, MonoFonts);
                    break;
                default:
                    throw new ValidationException(
                        $"unknown setting '{prop.Name}'. Allowed: {string.Join(", ", Keys)}",
                        code: "settings_unknown_key");
            }
        }

        return JsonSerializer.Serialize(cleaned);
    }

    private static double Number(JsonProperty prop, double min, double max)
    {
        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out var value))
            throw new ValidationException($"settings.{prop.Name} must be a number", code: "settings_invalid");
        if (double.IsNaN(value) || value < min || value > max)
            throw new ValidationException(
                $"settings.{prop.Name} must be between {min} and {max}", code: "settings_invalid");
        return value;
    }

    private static string Choice(JsonProperty prop, IReadOnlySet<string> allowed)
    {
        var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
        if (value is null || !allowed.Contains(value))
            throw new ValidationException(
                $"settings.{prop.Name} must be one of: {string.Join(", ", allowed.Order())}",
                code: "settings_invalid");
        return value;
    }
}
