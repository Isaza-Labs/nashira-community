using System.Collections.Frozen;
using System.Collections.ObjectModel;
using nashira_backend.Configuration.Modules;

namespace nashira_backend.BackgroundServices;

// Which capability each background worker belongs to, and therefore which ones this
// process starts. A hosted service is the one component that keeps acting with nobody
// asking: an HTTP endpoint of a disabled module simply never gets called, but a worker
// left registered goes on claiming jobs, dialling out to Slack and deleting rows in a
// deployment that was told not to run any of it.
//
// A worker whose responsibilities span capabilities is not listed under all of them —
// it is owned by the module that must always run it, and evaluates the rest per sweep.
// RetentionHostedService is the case: core has to keep pruning the authentication trail
// and the operational trail no matter what else is enabled, while the reports, jobs and
// webhook deliveries it also prunes belong to modules that may be off — and rows of a
// disabled module are preserved data, not garbage.
public static class HostedServiceCatalog
{
    private static readonly ReadOnlyCollection<HostedServiceRegistration> Registrations =
        Array.AsReadOnly<HostedServiceRegistration>(
        [
            // ── core ──────────────────────────────────────────────────────────
            // The operational trail. The queue outlives the request that filled it and
            // this is the only thing allowed to drain it, so nothing may switch it off.
            Own<TraceWriterHostedService>(ModuleId.Core),
            // Settings an admin may change without a redeploy.
            Own<AppSettingsRefreshHostedService>(ModuleId.Core),
            // The daily objective sweep. It writes an audit row and pages nobody, which
            // is why it belongs to core: an outage is exactly when a notification path
            // is least likely to work, and the row survives it.
            Own<SloBreachWatcherService>(ModuleId.Core),
            // Mixed by nature; see the note above. Guarded responsibility by
            // responsibility inside its own sweep.
            Own<RetentionHostedService>(ModuleId.Core),

            // ── automation ────────────────────────────────────────────────────
            // The job queue's two halves: cron firings re-base, the worker claims with
            // SKIP LOCKED and executes. Both replica-safe, and both pointless — worse,
            // actively executing work — in a deployment without the engine.
            Own<SchedulerHostedService>(ModuleId.Automation),
            Own<JobWorkerHostedService>(ModuleId.Automation),
            // Installs the pip packages an admin approved on the python_snippet
            // allowlist. Nothing can reference a python snippet without automation, so
            // without it this would install packages for code that cannot run.
            Own<PythonPackageProvisionerHostedService>(ModuleId.Automation),

            // ── communications ────────────────────────────────────────────────
            // The messaging turn runs where the tool registry is populated — here.
            Own<MessagingWorkerHostedService>(ModuleId.Communications),
            Own<MessagingRetentionHostedService>(ModuleId.Communications),
            // No-public-ingress inbound: both dial OUT instead of waiting on a webhook,
            // which is precisely why leaving them registered would keep a disabled
            // deployment holding open connections to Slack and Teams.
            Own<SlackSocketModeHostedService>(ModuleId.Communications),
            Own<TeamsRelayHostedService>(ModuleId.Communications),
        ]);

    private static readonly FrozenDictionary<Type, IReadOnlyList<ModuleId>> Owners =
        Registrations.ToFrozenDictionary(
            registration => registration.Service,
            registration => registration.Modules);

    public static IReadOnlyCollection<Type> All => Owners.Keys;

    public static IReadOnlyList<ModuleId> RequirementsFor(Type service) => Owners[service];

    public static IReadOnlyList<Type> Enabled(ModuleSelection selection) =>
        [.. Registrations
            .Where(registration => selection.AreEnabled(registration.Modules))
            .Select(registration => registration.Service)];

    public static IServiceCollection AddModuleHostedServices(
        this IServiceCollection services,
        ModuleSelection selection)
    {
        foreach (var registration in Registrations.Where(r => selection.AreEnabled(r.Modules)))
            registration.Register(services);

        return services;
    }

    private static HostedServiceRegistration Own<TService>(params ModuleId[] modules)
        where TService : class, IHostedService =>
        new(
            typeof(TService),
            Array.AsReadOnly(modules),
            services => services.AddHostedService<TService>());
}

public sealed record HostedServiceRegistration(
    Type Service,
    IReadOnlyList<ModuleId> Modules,
    Action<IServiceCollection> Register);
