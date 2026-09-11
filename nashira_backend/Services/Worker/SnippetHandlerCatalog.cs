using System.Collections.Frozen;
using System.Collections.ObjectModel;
using nashira_backend.Configuration.Modules;
using nashira_backend.Services.Worker.Handlers;

namespace nashira_backend.Services.Worker;

// Which capability each snippet type belongs to, and therefore which handlers a
// deployment registers. A snippet whose capability is off must not enter the registry:
// the reference checker refuses a workflow that names an unknown snippet type, which
// turns "this deployment cannot do that" into an error at authoring time instead of a
// run that reaches a device and fails there.
//
// automation is required on top of the declared module for every entry — a snippet
// without the engine that runs it is nothing — so the base handlers declare automation
// itself and the combined ones name only what they add. This is the intersection the
// specification describes: ssh needs automation and fleet, mcp_call needs automation and
// integrations, git needs automation and git, report needs automation and artifacts.
public static class SnippetHandlerCatalog
{
    // The workflow engine itself. Nothing here runs without it.
    private const ModuleId Engine = ModuleId.Automation;

    private static readonly ReadOnlyCollection<SnippetHandlerRegistration> Registrations =
        Array.AsReadOnly<SnippetHandlerRegistration>(
        [
            // ── the engine's own vocabulary ───────────────────────────────────
            Own<TransformSnippetHandler>(ModuleId.Automation),
            // Takes a raw URL or a catalogued operation; neither needs a configured
            // integration, and secret references resolve through core's own resolver.
            Own<RestCallSnippetHandler>(ModuleId.Automation),
            Own<PythonSnippetHandler>(ModuleId.Automation),
            // Accepts an IP as readily as an inventory name, so reachability stays
            // available to a deployment that runs no fleet at all.
            Own<PingSnippetHandler>(ModuleId.Automation),

            // ── intersections ─────────────────────────────────────────────────
            Own<SshSnippetHandler>(ModuleId.Fleet),
            Own<AnsiblePlaybookSnippetHandler>(ModuleId.Fleet),
            Own<McpCallSnippetHandler>(ModuleId.Integrations),
            Own<IntegrationActionSnippetHandler>(ModuleId.Integrations),
            Own<GitSnippetHandler>(ModuleId.Git),
            Own<ReportSnippetHandler>(ModuleId.Artifacts),
            Own<EmailSendSnippetHandler>(ModuleId.Communications),
            Own<EmailMailboxSnippetHandler>(ModuleId.Communications),
            Own<SlackMessageSnippetHandler>(ModuleId.Communications),

            // Stubs, registered on purpose: a reference to them fails with an
            // actionable message rather than `unknown_handler` — but only where the
            // capability they stand for is enabled, or the message would be wrong.
            Own<NetconfSnippetHandler>(ModuleId.Fleet),
            Own<SnmpV3SnippetHandler>(ModuleId.Fleet),
        ]);

    private static readonly FrozenDictionary<Type, IReadOnlyList<ModuleId>> Owners =
        Registrations.ToFrozenDictionary(
            registration => registration.Handler,
            registration => registration.Modules);

    public static IReadOnlyCollection<Type> All => Owners.Keys;

    public static IReadOnlyList<ModuleId> RequirementsFor(Type handler) =>
        [Engine, .. Owners[handler]];

    public static IReadOnlyList<Type> Enabled(ModuleSelection selection) =>
        selection.IsEnabled(Engine)
            ? [.. Registrations
                .Where(registration => selection.AreEnabled(registration.Modules))
                .Select(registration => registration.Handler)]
            : [];

    private static SnippetHandlerRegistration Own<THandler>(params ModuleId[] modules)
        where THandler : class, ISnippetHandler =>
        new(typeof(THandler), Array.AsReadOnly(modules));
}

public sealed record SnippetHandlerRegistration(Type Handler, IReadOnlyList<ModuleId> Modules);
