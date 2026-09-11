using nashira_backend.Configuration.Modules;
using nashira_backend.Data.DTos.Modules;

namespace nashira_backend.Services.Modules;

// Projects the resolved selection onto the public manifest. The wire document is built
// from the catalog and the selection only — never from the raw NASHIRA_MODULES text, so
// the SPA sees the effective state and no deployment string ever leaves the process.
public static class ModuleManifest
{
    public static ModuleManifestResponse Build(ModuleSelection selection) =>
        new(
            ConfigurationMode: Describe(selection.Mode),
            Modules: ModuleCatalog.All
                .Select(definition => new ModuleManifestEntry(
                    Id: definition.Key,
                    Enabled: selection.IsEnabled(definition.Id),
                    Configurable: definition.Configurable,
                    Dependencies: definition.Dependencies
                        .Select(dependency => ModuleCatalog.Get(dependency).Key)
                        .ToArray()))
                .ToArray());

    // Wire spellings of ModuleConfigurationMode. Kept explicit rather than derived from
    // the enum name: the contract says default_all, and enum renames must not reach it.
    private static string Describe(ModuleConfigurationMode mode) => mode switch
    {
        ModuleConfigurationMode.DefaultAll => "default_all",
        ModuleConfigurationMode.Explicit => "explicit",
        _ => throw new ArgumentOutOfRangeException(
            nameof(mode), mode, "Unmapped module configuration mode"),
    };
}
