using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text;

namespace nashira_backend.Configuration.Modules;

// Immutable, validated answer to "what can this process activate?". Parse is pure
// so startup, tests and future deployment adapters all share exactly one rule.
public sealed class ModuleSelection
{
    public const string EnvironmentVariableName = "NASHIRA_MODULES";

    private readonly FrozenSet<ModuleId> _enabled;

    private ModuleSelection(ModuleConfigurationMode mode, IEnumerable<ModuleId> enabled)
    {
        Mode = mode;
        var ordered = ModuleCatalog.All
            .Select(module => module.Id)
            .Where(enabled.Contains)
            .ToArray();
        Enabled = Array.AsReadOnly(ordered);
        _enabled = ordered.ToFrozenSet();
    }

    public ModuleConfigurationMode Mode { get; }
    public IReadOnlyList<ModuleId> Enabled { get; }

    public bool IsEnabled(ModuleId module) => _enabled.Contains(module);

    public bool AreEnabled(IEnumerable<ModuleId> modules) => modules.All(IsEnabled);

    public static ModuleSelection Parse(string? raw)
    {
        if (raw is null)
            return new ModuleSelection(
                ModuleConfigurationMode.DefaultAll,
                ModuleCatalog.All.Select(module => module.Id));

        var errors = new List<string>();
        var selected = new HashSet<ModuleId> { ModuleId.Core };
        var unknownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(raw))
        {
            errors.Add("NASHIRA_MODULES is defined but empty");
        }
        else
        {
            var hasEmptyElement = false;
            foreach (var item in raw.Split(','))
            {
                var key = item.Trim();
                if (key.Length == 0)
                {
                    hasEmptyElement = true;
                    continue;
                }

                if (ModuleCatalog.TryGet(key, out var module))
                    selected.Add(module.Id);
                else
                    unknownKeys.Add(key);
            }

            if (hasEmptyElement)
                errors.Add("NASHIRA_MODULES contains an empty module");
        }

        var validKeys = string.Join(", ", ModuleCatalog.All.Select(module => module.Key));
        foreach (var key in unknownKeys.Order(StringComparer.OrdinalIgnoreCase))
            errors.Add($"module \"{key}\" is not recognized; valid modules: {validKeys}");

        AddDependencyErrors(selected, errors);

        if (errors.Count > 0)
        {
            var enabledKeys = ModuleCatalog.All
                .Where(module => module.Configurable && selected.Contains(module.Id))
                .Select(module => module.Key);
            throw new ModuleConfigurationException(errors, enabledKeys);
        }

        return new ModuleSelection(ModuleConfigurationMode.Explicit, selected);
    }

    private static void AddDependencyErrors(
        IReadOnlySet<ModuleId> selected,
        ICollection<string> errors)
    {
        foreach (var module in ModuleCatalog.All.Where(module =>
                     module.Id != ModuleId.Core && selected.Contains(module.Id)))
        {
            foreach (var dependency in module.Dependencies)
            {
                if (selected.Contains(dependency))
                    continue;

                errors.Add(
                    $"module \"{module.Key}\" requires module \"{ModuleCatalog.Get(dependency).Key}\"");
                AddMissingDescendants(module, dependency, [dependency], selected, errors);
            }
        }
    }

    private static void AddMissingDescendants(
        ModuleDefinition root,
        ModuleId current,
        IReadOnlyList<ModuleId> path,
        IReadOnlySet<ModuleId> selected,
        ICollection<string> errors)
    {
        foreach (var dependency in ModuleCatalog.Get(current).Dependencies)
        {
            if (selected.Contains(dependency) || path.Contains(dependency))
                continue;

            var via = string.Join(
                "\" -> \"",
                path.Select(module => ModuleCatalog.Get(module).Key));
            errors.Add(
                $"module \"{root.Key}\" requires module \"{ModuleCatalog.Get(dependency).Key}\" "
                + $"through \"{via}\"");
            AddMissingDescendants(
                root,
                dependency,
                [.. path, dependency],
                selected,
                errors);
        }
    }
}

public sealed class ModuleConfigurationException : InvalidOperationException
{
    public ModuleConfigurationException(
        IEnumerable<string> errors,
        IEnumerable<string> enabledModules)
        : this(
            new ReadOnlyCollection<string>(errors.ToArray()),
            enabledModules.ToArray())
    {
    }

    private ModuleConfigurationException(
        IReadOnlyList<string> errors,
        IReadOnlyList<string> enabledModules)
        : base(BuildMessage(errors, enabledModules))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }

    private static string BuildMessage(
        IEnumerable<string> errors,
        IReadOnlyCollection<string> enabledModules)
    {
        var message = new StringBuilder("Module configuration is invalid:");
        foreach (var error in errors)
            message.Append("\n- ").Append(error);

        message.Append("\n- enabled modules: ");
        message.Append(enabledModules.Count == 0 ? "(none)" : string.Join(", ", enabledModules));
        return message.ToString();
    }
}
