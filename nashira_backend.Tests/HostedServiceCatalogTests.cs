using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using nashira_backend.BackgroundServices;
using nashira_backend.Configuration.Modules;

namespace nashira_backend.Tests;

// Background workers against the deployment's capabilities. A worker is the component
// that keeps acting with nobody asking, so "registered but idle" is not good enough:
// a disabled capability must leave nothing running.
public class HostedServiceCatalogTests
{
    private static IReadOnlyList<Type> Implementations() =>
        [.. typeof(HostedServiceCatalog).Assembly.GetTypes()
            .Where(type => typeof(IHostedService).IsAssignableFrom(type)
                && type is { IsAbstract: false, IsInterface: false })];

    private static IReadOnlyList<string> Names(IEnumerable<Type> types) =>
        [.. types.Select(type => type.Name).Order(StringComparer.Ordinal)];

    // Fails when somebody writes a worker and forgets it exists.
    [Fact]
    public void Every_hosted_service_is_classified()
    {
        Assert.Empty(Names(Implementations().Except(HostedServiceCatalog.All)));
    }

    // What the registration actually does, not just what the table says: the services
    // reaching the container are the enabled ones and only those.
    private static IReadOnlyList<string> Started(string? configuredModules)
    {
        var services = new ServiceCollection();
        services.AddModuleHostedServices(ModuleSelection.Parse(configuredModules));

        return [.. services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType?.Name
                ?? descriptor.ImplementationFactory?.Method.ReturnType.Name
                ?? "unknown")
            .Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void An_absent_variable_starts_every_worker()
    {
        Assert.Equal(Names(HostedServiceCatalog.All), Started(null));
    }

    // The core-only deployment: the trail keeps being written and pruned, settings keep
    // refreshing, objectives keep being swept — and nothing claims a job or dials out.
    [Fact]
    public void A_core_only_deployment_starts_only_the_workers_core_owns()
    {
        Assert.Equal(
            [
                nameof(AppSettingsRefreshHostedService),
                nameof(RetentionHostedService),
                nameof(SloBreachWatcherService),
                nameof(TraceWriterHostedService),
            ],
            Started("core"));
    }

    [Fact]
    public void The_engine_workers_need_automation()
    {
        var started = Started("automation");

        Assert.Contains(nameof(SchedulerHostedService), started);
        Assert.Contains(nameof(JobWorkerHostedService), started);
        Assert.Contains(nameof(PythonPackageProvisionerHostedService), started);

        var withoutAutomation = Started("fleet,git");
        Assert.DoesNotContain(nameof(SchedulerHostedService), withoutAutomation);
        Assert.DoesNotContain(nameof(JobWorkerHostedService), withoutAutomation);
        Assert.DoesNotContain(nameof(PythonPackageProvisionerHostedService), withoutAutomation);
    }

    // Slack and Teams dial out rather than wait on a webhook, which is exactly why
    // leaving them registered would keep a disabled deployment talking to them.
    [Fact]
    public void The_channel_workers_need_communications()
    {
        var started = Started("communications,chat,ai-studio,integrations");

        Assert.Contains(nameof(MessagingWorkerHostedService), started);
        Assert.Contains(nameof(MessagingRetentionHostedService), started);
        Assert.Contains(nameof(SlackSocketModeHostedService), started);
        Assert.Contains(nameof(TeamsRelayHostedService), started);

        // chat alone is not communications: the turn runner is there, the channels
        // are not.
        var chatOnly = Started("chat,ai-studio,integrations");
        Assert.DoesNotContain(nameof(MessagingWorkerHostedService), chatOnly);
        Assert.DoesNotContain(nameof(SlackSocketModeHostedService), chatOnly);
        Assert.DoesNotContain(nameof(TeamsRelayHostedService), chatOnly);
    }

    // Retention spans capabilities and is owned by core, because the authentication and
    // operational trails have to be pruned in every deployment. Splitting it in two
    // would have meant two schedules pruning one table each; it evaluates its
    // responsibilities per sweep instead — the scope below.
    [Fact]
    public void Retention_runs_everywhere_and_is_owned_by_core()
    {
        Assert.Equal(
            [ModuleId.Core],
            HostedServiceCatalog.RequirementsFor(typeof(RetentionHostedService)));
        Assert.Contains(nameof(RetentionHostedService), Started("core"));
    }

    // The trails core writes in every deployment are pruned in every deployment: they
    // take rows from unauthenticated callers and from every mutating request, so the
    // capability nobody switched off would otherwise be the one that fills the disk.
    [Theory]
    [InlineData(null)]
    [InlineData("core")]
    [InlineData("chat,ai-studio,integrations")]
    public void Retention_always_prunes_the_trails_core_owns(string? configuredModules)
    {
        var scope = RetentionScope.For(ModuleSelection.Parse(configuredModules));

        Assert.True(scope.AuthEvents);
        Assert.True(scope.Traces);
    }

    // Rows of a disabled module are data a later deployment expects to find. An expiry
    // nobody can act on is not a reason to delete.
    [Fact]
    public void Retention_leaves_the_data_of_a_disabled_capability_alone()
    {
        var scope = RetentionScope.For(ModuleSelection.Parse("core"));

        Assert.False(scope.Reports);
        Assert.False(scope.Jobs);
        Assert.False(scope.WebhookDeliveries);
    }

    [Fact]
    public void Retention_prunes_a_capability_that_is_enabled()
    {
        Assert.True(RetentionScope.For(ModuleSelection.Parse("artifacts")).Reports);
        Assert.True(RetentionScope.For(ModuleSelection.Parse("automation")).Jobs);
        Assert.True(RetentionScope.For(ModuleSelection.Parse("git")).WebhookDeliveries);

        var everything = RetentionScope.For(ModuleSelection.Parse(null));
        Assert.Equal(new RetentionScope(true, true, true, true, true), everything);
    }

    // Every worker that stops is one whose capability was switched off, and every
    // worker that starts is one this deployment can actually support.
    [Theory]
    [InlineData(null)]
    [InlineData("core")]
    [InlineData("chat,ai-studio,integrations,secrets")]
    [InlineData("automation,fleet")]
    [InlineData("communications,chat,ai-studio,integrations")]
    public void A_started_worker_always_has_its_capabilities(string? configuredModules)
    {
        var selection = ModuleSelection.Parse(configuredModules);

        Assert.All(
            HostedServiceCatalog.Enabled(selection),
            service => Assert.True(selection.AreEnabled(HostedServiceCatalog.RequirementsFor(service))));
        Assert.Equal(Names(HostedServiceCatalog.Enabled(selection)), Started(configuredModules));
    }
}
