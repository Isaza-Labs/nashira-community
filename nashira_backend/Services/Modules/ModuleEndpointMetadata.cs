using nashira_backend.Configuration.Modules;

namespace nashira_backend.Services.Modules;

// The modules an HTTP endpoint needs. Ownership is authored in EndpointModuleCatalog
// and stamped here by ModuleControllerConvention — a single table is what makes
// "every action is classified" a property one can check, which per-controller
// attributes never are. Applied directly it still works, and then reads as an extra
// requirement on top of the catalog's: the endpoint needs both.
//
// Core endpoints carry it too — with ModuleId.Core — so "always available" stays
// distinguishable from "nobody classified this yet".
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Method,
    AllowMultiple = false,
    Inherited = true)]
public sealed class RequiresModuleAttribute : Attribute
{
    public RequiresModuleAttribute(params ModuleId[] modules)
    {
        if (modules.Length == 0)
            throw new ArgumentException(
                "RequiresModule needs at least one module; use ModuleId.Core for always-on endpoints.",
                nameof(modules));

        RequiredModules = Array.AsReadOnly(modules);
    }

    public IReadOnlyList<ModuleId> RequiredModules { get; }
}

// Reads endpoint requirements out of MVC metadata. Kept apart from the filter so the
// OpenAPI document and the availability gate answer the same question the same way.
public static class ModuleEndpointRequirements
{
    public static IReadOnlyList<ModuleId> For(IEnumerable<object> endpointMetadata) =>
        endpointMetadata
            .OfType<RequiresModuleAttribute>()
            .SelectMany(requirement => requirement.RequiredModules)
            .Distinct()
            .ToArray();

    // The module reported to the caller when several are missing. Catalog order rather
    // than metadata order, so the same request never reports a different module between
    // two deployments that only differ in how the attributes were written.
    public static ModuleId? FirstDisabled(
        IEnumerable<object> endpointMetadata,
        ModuleSelection selection)
    {
        var required = For(endpointMetadata);
        if (required.Count == 0)
            return null;

        foreach (var module in ModuleCatalog.All.Select(definition => definition.Id))
        {
            if (required.Contains(module) && !selection.IsEnabled(module))
                return module;
        }

        return null;
    }
}
