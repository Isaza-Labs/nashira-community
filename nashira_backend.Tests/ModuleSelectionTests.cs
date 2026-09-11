using nashira_backend.Configuration.Modules;

namespace nashira_backend.Tests;

public class ModuleSelectionTests
{
    [Fact]
    public void Catalog_contains_the_approved_modules_and_dependencies()
    {
        Assert.Equal(
            [
                "core",
                "chat",
                "ai-studio",
                "automation",
                "fleet",
                "integrations",
                "communications",
                "secrets",
                "git",
                "knowledge",
                "artifacts",
                "governance",
                "observability",
            ],
            ModuleCatalog.All.Select(module => module.Key));

        Assert.False(ModuleCatalog.Get(ModuleId.Core).Configurable);
        Assert.Equal(
            [ModuleId.AiStudio],
            ModuleCatalog.Get(ModuleId.Chat).Dependencies);
        Assert.Equal(
            [ModuleId.Integrations],
            ModuleCatalog.Get(ModuleId.AiStudio).Dependencies);
        Assert.Equal(
            [ModuleId.Chat],
            ModuleCatalog.Get(ModuleId.Communications).Dependencies);
        Assert.All(
            ModuleCatalog.All.Where(module =>
                module.Id is not ModuleId.Core
                    and not ModuleId.Chat
                    and not ModuleId.AiStudio
                    and not ModuleId.Communications),
            module => Assert.Equal([ModuleId.Core], module.Dependencies));
    }

    [Fact]
    public void Missing_variable_enables_every_module_for_backwards_compatibility()
    {
        var selection = ModuleSelection.Parse(null);

        Assert.Equal(ModuleConfigurationMode.DefaultAll, selection.Mode);
        Assert.Equal(ModuleCatalog.All.Count, selection.Enabled.Count);
        Assert.All(ModuleCatalog.All, module => Assert.True(selection.IsEnabled(module.Id)));
    }

    [Fact]
    public void Explicit_list_enables_only_its_modules_and_core()
    {
        var selection = ModuleSelection.Parse("chat,ai-studio,integrations,secrets");

        Assert.Equal(ModuleConfigurationMode.Explicit, selection.Mode);
        Assert.Equal(
            [ModuleId.Core, ModuleId.Chat, ModuleId.AiStudio, ModuleId.Integrations, ModuleId.Secrets],
            selection.Enabled);
        Assert.False(selection.IsEnabled(ModuleId.Automation));
        Assert.True(selection.AreEnabled([ModuleId.Chat, ModuleId.Secrets]));
        Assert.False(selection.AreEnabled([ModuleId.Chat, ModuleId.Fleet]));
    }

    [Fact]
    public void Values_are_trimmed_case_insensitive_and_deduplicated()
    {
        var selection = ModuleSelection.Parse(" Chat, AI-STUDIO,chat , Integrations, Secrets ");

        Assert.Equal(
            [ModuleId.Core, ModuleId.Chat, ModuleId.AiStudio, ModuleId.Integrations, ModuleId.Secrets],
            selection.Enabled);
    }

    [Fact]
    public void Explicit_core_is_accepted_as_a_redundant_value()
    {
        var selection = ModuleSelection.Parse("core,chat,ai-studio,integrations");

        Assert.Equal(
            [ModuleId.Core, ModuleId.Chat, ModuleId.AiStudio, ModuleId.Integrations],
            selection.Enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_explicit_value_is_rejected(string raw)
    {
        var error = Assert.Throws<ModuleConfigurationException>(
            () => ModuleSelection.Parse(raw));

        Assert.Contains("NASHIRA_MODULES is defined but empty", error.Message);
    }

    [Theory]
    [InlineData("chat,,ai-studio")]
    [InlineData(",chat,ai-studio")]
    [InlineData("chat,ai-studio,")]
    public void Empty_list_element_is_rejected(string raw)
    {
        var error = Assert.Throws<ModuleConfigurationException>(
            () => ModuleSelection.Parse(raw));

        Assert.Contains("NASHIRA_MODULES contains an empty module", error.Message);
    }

    [Fact]
    public void Unknown_module_is_rejected_and_valid_values_are_reported()
    {
        var error = Assert.Throws<ModuleConfigurationException>(
            () => ModuleSelection.Parse("chat,ai-studio,integrations,unknown"));

        Assert.Contains("module \"unknown\" is not recognized", error.Message);
        Assert.Contains("valid modules:", error.Message);
        Assert.Contains("observability", error.Message);
    }

    [Fact]
    public void Missing_direct_dependency_is_rejected_without_enabling_it()
    {
        var error = Assert.Throws<ModuleConfigurationException>(
            () => ModuleSelection.Parse("chat,secrets"));

        Assert.Contains("module \"chat\" requires module \"ai-studio\"", error.Message);
        Assert.Contains("enabled modules: chat, secrets", error.Message);
    }

    [Fact]
    public void Ai_studio_without_integrations_is_rejected()
    {
        var error = Assert.Throws<ModuleConfigurationException>(
            () => ModuleSelection.Parse("ai-studio"));

        Assert.Contains(
            "module \"ai-studio\" requires module \"integrations\"",
            error.Message);
    }

    [Fact]
    public void All_missing_transitive_dependencies_are_reported_together()
    {
        var error = Assert.Throws<ModuleConfigurationException>(
            () => ModuleSelection.Parse("communications"));

        Assert.Contains(
            "module \"communications\" requires module \"chat\"",
            error.Message);
        Assert.Contains(
            "module \"communications\" requires module \"ai-studio\" through \"chat\"",
            error.Message);
        Assert.Contains(
            "module \"communications\" requires module \"integrations\" through \"chat\" -> \"ai-studio\"",
            error.Message);
    }

    [Fact]
    public void Complete_transitive_dependency_chain_is_valid()
    {
        var selection = ModuleSelection.Parse("communications,chat,ai-studio,integrations");

        Assert.True(selection.AreEnabled(
            [ModuleId.Communications, ModuleId.Chat, ModuleId.AiStudio, ModuleId.Integrations]));
    }

    [Fact]
    public void Independent_modules_require_only_implicit_core()
    {
        var selection = ModuleSelection.Parse(
            "automation,fleet,integrations,secrets,git,knowledge,artifacts,governance,observability");

        Assert.False(selection.IsEnabled(ModuleId.Chat));
        Assert.False(selection.IsEnabled(ModuleId.AiStudio));
        Assert.True(selection.IsEnabled(ModuleId.Core));
    }

    [Fact]
    public void Syntax_unknowns_and_dependencies_are_aggregated_in_one_error()
    {
        var error = Assert.Throws<ModuleConfigurationException>(
            () => ModuleSelection.Parse("chat,,unknown"));

        Assert.Collection(
            error.Errors,
            item => Assert.Contains("empty module", item),
            item => Assert.Contains("\"unknown\"", item),
            item => Assert.Contains("\"chat\"", item),
            item => Assert.Contains("\"integrations\"", item));
    }
}
