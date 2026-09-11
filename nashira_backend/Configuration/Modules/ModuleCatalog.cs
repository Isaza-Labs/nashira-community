using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace nashira_backend.Configuration.Modules;

public sealed record ModuleDefinition(
    ModuleId Id,
    string Key,
    bool Configurable,
    IReadOnlyList<ModuleId> Dependencies);

// The deployment capability graph. This is the only place that defines public
// module names and hard dependencies; component ownership is added in later slices.
public static class ModuleCatalog
{
    private static readonly ReadOnlyCollection<ModuleDefinition> Definitions =
        Array.AsReadOnly<ModuleDefinition>(
        [
            Define(ModuleId.Core, "core", configurable: false),
            Define(ModuleId.Chat, "chat", ModuleId.AiStudio),
            Define(ModuleId.AiStudio, "ai-studio", ModuleId.Integrations),
            Define(ModuleId.Automation, "automation", ModuleId.Core),
            Define(ModuleId.Fleet, "fleet", ModuleId.Core),
            Define(ModuleId.Integrations, "integrations", ModuleId.Core),
            Define(ModuleId.Communications, "communications", ModuleId.Chat),
            Define(ModuleId.Secrets, "secrets", ModuleId.Core),
            Define(ModuleId.Git, "git", ModuleId.Core),
            Define(ModuleId.Knowledge, "knowledge", ModuleId.Core),
            Define(ModuleId.Artifacts, "artifacts", ModuleId.Core),
            Define(ModuleId.Governance, "governance", ModuleId.Core),
            Define(ModuleId.Observability, "observability", ModuleId.Core),
        ]);

    private static readonly FrozenDictionary<ModuleId, ModuleDefinition> ById =
        Definitions.ToFrozenDictionary(module => module.Id);

    private static readonly FrozenDictionary<string, ModuleDefinition> ByKey =
        Definitions.ToFrozenDictionary(module => module.Key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ModuleDefinition> All => Definitions;

    public static ModuleDefinition Get(ModuleId id) => ById[id];

    public static bool TryGet(string key, out ModuleDefinition definition) =>
        ByKey.TryGetValue(key, out definition!);

    private static ModuleDefinition Define(
        ModuleId id,
        string key,
        params ModuleId[] dependencies) =>
        new(id, key, Configurable: true, Array.AsReadOnly(dependencies));

    private static ModuleDefinition Define(
        ModuleId id,
        string key,
        bool configurable,
        params ModuleId[] dependencies) =>
        new(id, key, configurable, Array.AsReadOnly(dependencies));
}
