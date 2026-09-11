using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Themes;

namespace nashira_backend.Tests;

// The style half of a theme is the only part the server vets, so these cover the
// boundary rather than the happy path alone: a shared theme's settings end up in
// font-family and border-radius declarations on everybody else's page.
public class ThemeSettingsTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static string Normalize(string raw) => ThemeSettingsValidator.Normalize(Json(raw));

    [Fact]
    public void Missing_settings_normalise_to_an_empty_object()
    {
        // A colours-only theme — every theme saved before this column existed —
        // has to stay valid and mean "inherit everything".
        Assert.Equal("{}", ThemeSettingsValidator.Normalize(null));
        Assert.Equal("{}", Normalize("null"));
        Assert.Equal("{}", Normalize("{}"));
    }

    [Fact]
    public void A_full_settings_object_round_trips()
    {
        var json = Normalize("""
            {"roundness":1.5,"ui_scale":1.1,"font_body":"system",
             "font_heading":"inherit","font_mono":"plex","heading_weight":700}
            """);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1.5, root.GetProperty("roundness").GetDouble());
        Assert.Equal(1.1, root.GetProperty("ui_scale").GetDouble());
        Assert.Equal("system", root.GetProperty("font_body").GetString());
        Assert.Equal("inherit", root.GetProperty("font_heading").GetString());
        Assert.Equal("plex", root.GetProperty("font_mono").GetString());
        Assert.Equal(700, root.GetProperty("heading_weight").GetInt32());
    }

    [Fact]
    public void A_partial_settings_object_keeps_only_what_it_set()
    {
        // Absent keys must not be materialised with defaults: "inherit app.css"
        // and "pinned to today's default" have to stay distinguishable.
        using var doc = JsonDocument.Parse(Normalize("""{"roundness":0.5}"""));
        Assert.Single(doc.RootElement.EnumerateObject());
        Assert.Equal(0.5, doc.RootElement.GetProperty("roundness").GetDouble());
    }

    [Theory]
    [InlineData("roundness", 0)]
    [InlineData("roundness", 2)]
    [InlineData("ui_scale", 0.85)]
    [InlineData("ui_scale", 1.15)]
    [InlineData("heading_weight", 300)]
    [InlineData("heading_weight", 900)]
    public void Range_endpoints_are_accepted(string key, double value)
    {
        var json = Normalize($$"""{"{{key}}":{{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""");
        Assert.Contains(key, json);
    }

    [Theory]
    [InlineData("roundness", -0.1)]
    [InlineData("roundness", 2.1)]
    [InlineData("ui_scale", 0.5)]
    [InlineData("ui_scale", 2)]
    [InlineData("heading_weight", 100)]
    [InlineData("heading_weight", 1000)]
    public void Out_of_range_numbers_are_rejected(string key, double value)
    {
        var raw = $$"""{"{{key}}":{{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""";
        var ex = Assert.Throws<ValidationException>(() => Normalize(raw));
        Assert.Equal("settings_invalid", ex.Code);
        Assert.Contains(key, ex.Message);
    }

    [Theory]
    [InlineData("""{"roundness":"1"}""")]
    [InlineData("""{"ui_scale":true}""")]
    [InlineData("""{"heading_weight":null}""")]
    public void Numbers_given_as_something_else_are_rejected(string raw)
    {
        Assert.Equal("settings_invalid", Assert.Throws<ValidationException>(() => Normalize(raw)).Code);
    }

    [Theory]
    [InlineData("font_body", "plex")]
    [InlineData("font_body", "serif")]
    [InlineData("font_heading", "inherit")]
    [InlineData("font_heading", "mono")]
    [InlineData("font_mono", "system")]
    public void Known_font_choices_are_accepted(string key, string value)
    {
        Assert.Contains(value, Normalize($$"""{"{{key}}":"{{value}}"}"""));
    }

    [Theory]
    // "inherit" is a heading-only option: a body font that inherits from itself
    // means nothing, and letting it through would emit an unusable stack.
    [InlineData("font_body", "inherit")]
    // The mono slot has its own, shorter vocabulary — a sans key is not valid there.
    [InlineData("font_mono", "serif")]
    // The whole point of the closed list: no theme gets to name a remote webfont.
    [InlineData("font_body", "'Comic Sans MS', cursive")]
    [InlineData("font_heading", "https://fonts.example/evil.css")]
    public void Unknown_font_choices_are_rejected(string key, string value)
    {
        var ex = Assert.Throws<ValidationException>(() => Normalize($$"""{"{{key}}":"{{value}}"}"""));
        Assert.Equal("settings_invalid", ex.Code);
    }

    [Fact]
    public void An_unknown_key_is_an_error_rather_than_silently_dropped()
    {
        // Dropping it would leave someone certain they saved a setting the app
        // then ignores forever.
        var ex = Assert.Throws<ValidationException>(() => Normalize("""{"letter_spacing":0.1}"""));
        Assert.Equal("settings_unknown_key", ex.Code);
        Assert.Contains("letter_spacing", ex.Message);
        Assert.Contains("roundness", ex.Message);
    }

    [Fact]
    public void A_non_object_payload_is_rejected()
    {
        Assert.Equal("settings_invalid", Assert.Throws<ValidationException>(() => Normalize("[]")).Code);
        Assert.Equal("settings_invalid", Assert.Throws<ValidationException>(() => Normalize("\"roundness\"")).Code);
    }

    [Fact]
    public void Heading_weight_is_stored_as_an_integer()
    {
        // It lands in a CSS font-weight, where 650.4 is meaningless — and the
        // editor has to read back exactly what its select sent.
        using var doc = JsonDocument.Parse(Normalize("""{"heading_weight":650.4}"""));
        Assert.Equal(650, doc.RootElement.GetProperty("heading_weight").GetInt32());
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("heading_weight").ValueKind);
    }
}
