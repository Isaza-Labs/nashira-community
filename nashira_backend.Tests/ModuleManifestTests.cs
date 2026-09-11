using System.Text.Json;
using nashira_backend.Configuration.Modules;
using nashira_backend.Services.Modules;

namespace nashira_backend.Tests;

// GET /api/modules is what the SPA bootstraps from: effective state, public keys, and
// disabled modules included so a missing page is diagnosable instead of invisible.
public class ModuleManifestTests
{
    private static JsonElement Serialize(ModuleSelection selection) =>
        JsonSerializer.SerializeToElement(
            ModuleManifest.Build(selection),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static JsonElement Module(JsonElement manifest, string id) =>
        manifest.GetProperty("modules").EnumerateArray()
            .Single(module => module.GetProperty("id").GetString() == id);

    [Fact]
    public void An_absent_variable_reports_default_all_with_everything_enabled()
    {
        var manifest = Serialize(ModuleSelection.Parse(null));

        Assert.Equal("default_all", manifest.GetProperty("configuration_mode").GetString());
        Assert.All(
            manifest.GetProperty("modules").EnumerateArray(),
            module => Assert.True(module.GetProperty("enabled").GetBoolean()));
    }

    [Fact]
    public void An_explicit_selection_reports_explicit_mode()
    {
        var manifest = Serialize(ModuleSelection.Parse("chat,ai-studio,integrations,secrets"));

        Assert.Equal("explicit", manifest.GetProperty("configuration_mode").GetString());
    }

    // Disabled modules stay in the document. Dropping them would leave the SPA and an
    // operator unable to tell "this module is off here" from "this build has no such
    // module", which is the difference between a fix and a bug report.
    [Fact]
    public void Disabled_modules_are_reported_rather_than_omitted()
    {
        var manifest = Serialize(ModuleSelection.Parse("chat,ai-studio,integrations,secrets"));

        Assert.Equal(
            ModuleCatalog.All.Count,
            manifest.GetProperty("modules").GetArrayLength());
        Assert.True(Module(manifest, "chat").GetProperty("enabled").GetBoolean());
        Assert.False(Module(manifest, "automation").GetProperty("enabled").GetBoolean());
    }

    // core is implicit in every selection and cannot be turned off, so the manifest has
    // to say so: a client must never render a toggle that the backend would reject.
    [Fact]
    public void Core_is_enabled_and_not_configurable()
    {
        var core = Module(Serialize(ModuleSelection.Parse("secrets")), "core");

        Assert.True(core.GetProperty("enabled").GetBoolean());
        Assert.False(core.GetProperty("configurable").GetBoolean());
        Assert.Empty(core.GetProperty("dependencies").EnumerateArray());
    }

    [Fact]
    public void Dependencies_are_reported_with_public_keys()
    {
        var manifest = Serialize(ModuleSelection.Parse(null));
        var chat = Module(manifest, "chat");
        var aiStudio = Module(manifest, "ai-studio");

        Assert.True(chat.GetProperty("configurable").GetBoolean());
        Assert.Equal(
            "ai-studio",
            Assert.Single(chat.GetProperty("dependencies").EnumerateArray()).GetString());
        Assert.Equal(
            "integrations",
            Assert.Single(aiStudio.GetProperty("dependencies").EnumerateArray()).GetString());
    }

    // The manifest is the effective state, never the raw variable: enum names and the
    // deployment's own NASHIRA_MODULES text stay inside the process.
    [Fact]
    public void The_document_uses_catalog_keys_only()
    {
        var manifest = Serialize(ModuleSelection.Parse(" Chat , ai-studio, integrations "));

        Assert.Equal(
            ModuleCatalog.All.Select(definition => definition.Key).ToArray(),
            manifest.GetProperty("modules").EnumerateArray()
                .Select(module => module.GetProperty("id").GetString())
                .ToArray());
        Assert.DoesNotContain("AiStudio", manifest.GetRawText());
    }
}
