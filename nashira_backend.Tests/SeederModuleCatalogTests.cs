using System.Reflection;
using nashira_backend.Configuration.Modules;
using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Ai.SelfCorrection;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;

namespace nashira_backend.Tests;

// Boot-time seeding against the deployment's capabilities. Seeding writes rows, so the
// question is not whether a baseline is harmless but whether this deployment asked for
// it — and, when a module comes back, whether its baseline returns over the data that
// was kept rather than beside it.
public class SeederModuleCatalogTests
{
    // A seeder is a static class that writes a baseline at boot: a public static
    // Seed…Async. Discovered rather than listed, so a new one cannot arrive unnoticed.
    private static IReadOnlyList<Type> Implementations() =>
        [.. typeof(SeederModuleCatalog).Assembly.GetTypes()
            .Where(type => type.IsAbstract && type.IsSealed
                && type.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(method =>
                    method.Name.StartsWith("Seed", StringComparison.Ordinal)
                    && typeof(Task).IsAssignableFrom(method.ReturnType)))];

    private static IReadOnlyList<string> Names(IEnumerable<Type> types) =>
        [.. types.Select(type => type.Name).Order(StringComparer.Ordinal)];

    private static IReadOnlyList<string> Ran(string? configuredModules, bool isDevelopment = true) =>
        Names(SeederModuleCatalog.Enabled(ModuleSelection.Parse(configuredModules), isDevelopment));

    // Both directions, so the discovery rule cannot pass by finding nothing: every
    // seeder in the assembly is classified, and every classified seeder is a real one.
    [Fact]
    public void Every_seeder_is_classified()
    {
        Assert.Equal(Names(Implementations()), Names(SeederModuleCatalog.All));
    }

    [Fact]
    public void An_absent_variable_runs_every_seeder()
    {
        Assert.Equal(Names(SeederModuleCatalog.All), Ran(null));
    }

    // A deployment nobody can sign in to is not a smaller deployment, it is a broken
    // one — so core's seeder runs whatever else is selected.
    [Theory]
    [InlineData(null)]
    [InlineData("core")]
    [InlineData("chat,ai-studio,integrations,secrets")]
    [InlineData("automation,fleet")]
    public void The_core_seeder_runs_for_every_valid_selection(string? configuredModules)
    {
        Assert.Contains(nameof(DevSeedService), Ran(configuredModules));
    }

    // The whole point: a capability that is off installs nothing. Vendor commands with
    // no devices, an import allowlist for a sandbox nothing can invoke, a snippet
    // catalogue for an engine that does not run — all rows an operator would find
    // growing in the tables of a module they switched off.
    [Fact]
    public void A_core_only_deployment_seeds_nothing_but_core()
    {
        Assert.Equal([nameof(DevSeedService)], Ran("core"));
    }

    [Theory]
    [InlineData("ai-studio,integrations", nameof(BuiltinSpecSeeder))]
    [InlineData("ai-studio,integrations", nameof(LearningsSeedService))]
    [InlineData("knowledge", nameof(KnowledgeSeedService))]
    [InlineData("automation", nameof(PythonModuleSeeder))]
    [InlineData("automation", nameof(DefaultSnippetSeeder))]
    [InlineData("fleet", nameof(VendorCommandSeeder))]
    public void A_seeder_runs_exactly_where_its_capability_is_enabled(
        string configuredModules, string seeder)
    {
        Assert.Contains(seeder, Ran(configuredModules));
        Assert.DoesNotContain(seeder, Ran("core"));
    }

    // Nothing here deletes, and the selection is read fresh on every boot: switching a
    // module off and back on runs its seeder again, over the rows that stayed. Each one
    // is idempotent by key, which is what makes the second run a restore rather than a
    // duplication — the seeders' own tests hold that half.
    [Fact]
    public void Re_enabling_a_module_runs_its_seeder_again()
    {
        Assert.DoesNotContain(nameof(VendorCommandSeeder), Ran("automation"));
        Assert.Contains(nameof(VendorCommandSeeder), Ran("automation,fleet"));
        Assert.Equal(Ran(null), Names(SeederModuleCatalog.All));
    }

    // Production runs no dev seeder, and that is a different statement from a module
    // being disabled — both are decided in the catalog so neither gets decided twice.
    [Fact]
    public void The_development_seeder_never_runs_in_production()
    {
        Assert.DoesNotContain(nameof(DevSeedService), Ran(null, isDevelopment: false));
        Assert.Empty(Ran("core", isDevelopment: false));
        Assert.Contains(
            nameof(BuiltinSpecSeeder),
            Ran("ai-studio,integrations", isDevelopment: false));
    }

    // The baseline snippet catalogue is seeded from the handler registry, not from a
    // list of its own — so it follows the deployment's capabilities without this
    // catalog having to repeat them. This is the module half of that chain;
    // DefaultSnippetSeederTests holds the seeder half.
    [Fact]
    public void The_snippet_baseline_follows_the_handlers_the_deployment_registers()
    {
        var automationOnly = ModuleSelection.Parse("automation");

        Assert.Contains(nameof(DefaultSnippetSeeder), Ran("automation"));
        Assert.DoesNotContain(
            typeof(GitSnippetHandler),
            SnippetHandlerCatalog.Enabled(automationOnly));
        Assert.Contains(
            typeof(GitSnippetHandler),
            SnippetHandlerCatalog.Enabled(ModuleSelection.Parse("automation,git")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("core")]
    [InlineData("chat,ai-studio,integrations,secrets")]
    [InlineData("automation,fleet")]
    [InlineData("knowledge,git,artifacts")]
    public void A_seeder_that_runs_always_has_its_capabilities(string? configuredModules)
    {
        var selection = ModuleSelection.Parse(configuredModules);

        Assert.All(
            SeederModuleCatalog.Enabled(selection, isDevelopment: true),
            seeder => Assert.True(selection.AreEnabled(SeederModuleCatalog.RequirementsFor(seeder))));
    }
}
