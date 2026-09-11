using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Configuration.Modules;
using nashira_backend.Services.Trace;
using nashira_backend.Data.Models;

namespace nashira_backend.BackgroundServices;

// Deletes what retention says is gone.
//
// Owned by core because two of its duties are core's: the authentication trail is
// written by unauthenticated callers and needs a ceiling in every deployment, and the
// operational trail takes a row on every mutating request. The rest is swept per
// responsibility, only where that capability is enabled — rows belonging to a disabled
// module are data a later deployment expects to find, not garbage.
//
// Until this existed, ReportArtifact.ExpiresAt was a filter the list endpoint
// honoured — an expired report stopped being visible but its bytes stayed in the
// row forever, which is the opposite of what a retention period promises.
// Hard-deletes on purpose: retention is about the content ceasing to exist, and a
// soft-delete would keep exactly the bytes the policy said to drop.
public sealed class RetentionHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<AuthOptions> _auth;
    private readonly ModuleSelection _modules;
    private readonly ITraceLogger _trace;
    private readonly ILogger<RetentionHostedService> _logger;

    public RetentionHostedService(
        IServiceScopeFactory scopes, IOptions<AuthOptions> auth, ModuleSelection modules,
        ILogger<RetentionHostedService> logger, ITraceLogger trace)
    {
        _scopes = scopes;
        _auth = auth;
        _modules = modules;
        _trace = trace;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "retention.sweep_failed");
            }

            try { if (!await timer.WaitForNextTickAsync(ct)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    // Kept here rather than in configuration: a deployment that shortens this to save
    // disk is trading away the window in which an incident can still be investigated,
    // and that trade deserves a code change somebody reviews.
    private const int TraceRetentionDays = 14;

    // Webhook deliveries answer "did GitHub call us last Tuesday, and what did we do".
    // Past a month nobody is asking, and a repository with busy CI writes one of these
    // per push — the only table here whose growth an outsider controls.
    private const int WebhookDeliveryRetentionDays = 30;

    private async Task SweepAsync(CancellationToken ct)
    {
        using var services = _scopes.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var scope = RetentionScope.For(_modules);

        var reports = scope.Reports
            ? await db.ReportArtifacts
                .Where(r => r.ExpiresAt != null && r.ExpiresAt < now)
                .ExecuteDeleteAsync(ct)
            : 0;

        // Finished jobs are bookkeeping, not evidence — the durable record of what
        // ran is WorkflowRun and the audit chain. Thirty days covers "why did last
        // week's cron not fire" without accumulating forever.
        var cutoff = now.AddDays(-30);
        var jobs = scope.Jobs
            ? await db.Jobs
                .Where(j => (j.Status == Data.Models.Job.StatusSucceeded || j.Status == Data.Models.Job.StatusFailed)
                            && j.CompletedAt != null && j.CompletedAt < cutoff)
                .ExecuteDeleteAsync(ct)
            : 0;

        // The authentication trail. Unlike the audit chain this is not hash-linked, so
        // removing old rows leaves nothing inconsistent behind — and unlike the audit
        // chain it is written by unauthenticated callers, so it needs a ceiling.
        var authEvents = 0;
        var retentionDays = _auth.Value.AuthEventRetentionDays;
        if (scope.AuthEvents && retentionDays > 0)
        {
            var authCutoff = now.AddDays(-retentionDays);
            authEvents = await db.AuthEvents
                .Where(e => e.At < authCutoff)
                .ExecuteDeleteAsync(ct);
        }

        // The operational trail itself. It takes a row on every mutating request and on
        // every job, so it is by far the fastest-growing table here and the one most
        // certain to fill a disk if left alone. Two weeks: long enough to answer "what
        // happened last Tuesday", short enough to stay bounded.
        var traceCutoff = now.AddDays(-TraceRetentionDays);
        var traces = scope.Traces
            ? await db.TraceEvents
                .Where(t => t.At < traceCutoff)
                .ExecuteDeleteAsync(ct)
            : 0;

        var deliveryCutoff = now.AddDays(-WebhookDeliveryRetentionDays);
        var deliveries = scope.WebhookDeliveries
            ? await db.GitWebhookDeliveries
                .Where(d => d.At < deliveryCutoff)
                .ExecuteDeleteAsync(ct)
            : 0;

        if (reports > 0 || jobs > 0 || authEvents > 0 || traces > 0 || deliveries > 0)
        {
            _logger.LogInformation(
                "retention.swept reports={Reports} jobs={Jobs} auth_events={AuthEvents} traces={Traces} "
                + "git_webhook_deliveries={Deliveries}",
                reports, jobs, authEvents, traces, deliveries);
            // Recorded in the trail it just pruned: "the rows I wanted are gone" has
            // exactly one innocent explanation, and this is it.
            _trace.Event(TraceEvent.CategorySystem, "system.retention.sweep",
                new { reports, jobs, auth_events = authEvents, traces, git_webhook_deliveries = deliveries });
        }
    }
}

// Which of the sweep's responsibilities a deployment may act on.
//
// A separate type because the decision is the part worth checking and the sweep is not:
// every deletion here is ExecuteDelete, which no in-memory provider runs, so a test that
// wanted to observe the guards through the database could not exist.
//
// Auth events and traces are unconditional by design. Core writes both in every
// deployment — the authentication trail takes rows from unauthenticated callers, the
// operational trail one per mutating request — so core has to keep pruning them or the
// capability nobody switched off is the one that fills the disk.
public readonly record struct RetentionScope(
    bool Reports,
    bool Jobs,
    bool AuthEvents,
    bool Traces,
    bool WebhookDeliveries)
{
    public static RetentionScope For(ModuleSelection modules) => new(
        // Rows of a disabled module are data a later deployment expects to find. An
        // expiry nobody can act on is not a reason to delete.
        Reports: modules.IsEnabled(ModuleId.Artifacts),
        Jobs: modules.IsEnabled(ModuleId.Automation),
        AuthEvents: true,
        Traces: true,
        WebhookDeliveries: modules.IsEnabled(ModuleId.Git));
}
