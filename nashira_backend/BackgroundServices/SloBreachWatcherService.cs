using nashira_backend.Data.Models;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Slo;
using nashira_backend.Services.Trace;

namespace nashira_backend.BackgroundServices;

// The daily sweep that turns a missed objective into a durable record.
//
// The audit row is the alert primitive. Nothing here emails or pages anybody: an
// outage is exactly when a notification path is least likely to work, and a row that
// survives it answers "when did this start" afterwards. Whatever the deployment wires
// up to notify reads the trail, filtered on `slo.breach`.
//
// Written through AuditLogger rather than inserted directly — flow-weaver adds the row
// to its context and saves, which Nashira cannot do: every audit row carries a hash
// over its predecessor, and a row that skips the logger enters the chain unlinked and
// makes the verifier report the whole trail as broken from that point on.
public sealed class SloBreachWatcherService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    // A week: long enough that one bad afternoon does not trip every objective, short
    // enough that a breach still corresponds to something happening now.
    private const int Window = 7;

    // Migrations and seeding run at boot. Starting a sweep into a half-migrated schema
    // would fail noisily every restart for no reason.
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SloBreachWatcherService> _logger;

    public SloBreachWatcherService(
        IServiceScopeFactory scopes, ILogger<SloBreachWatcherService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Settle, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A sweep that throws must not take the loop down with it: tomorrow's
                // measurement is independent of today's failure to take one.
                _logger.LogError(ex, "slo.sweep.failed");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var compute = scope.ServiceProvider.GetRequiredService<SloComputeService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        var trace = scope.ServiceProvider.GetRequiredService<ITraceLogger>();

        var snapshot = await compute.ComputeAsync(Window, ct);
        var breaches = snapshot.Slos.Where(SloComputeService.IsBreach).ToList();

        if (breaches.Count == 0)
        {
            _logger.LogDebug("slo.sweep.clean window_days={Days}", snapshot.Days);
            // A clean sweep leaves no audit row by design, so without this there is no
            // way to tell "nothing was breached" from "the sweep never ran".
            trace.Event(TraceEvent.CategorySystem, "system.slo.sweep",
                new { window_days = snapshot.Days, breaches = 0 });
            return;
        }

        // Nobody is signed in, so the ambient actor is what puts a name on these rows
        // instead of leaving the trail's "who" blank for the one entry type that has no
        // human behind it by design.
        using var _ = AuditActor.Use(AuditActor.SloWatcher);

        foreach (var entry in breaches)
        {
            // One row per objective per sweep, so a breach that persists for a week
            // reads as seven days of breach rather than as a single moment last Monday.
            await audit.LogAsync(
                entityType: "slo",
                entityId: null,
                action: $"slo.breach.{entry.Key}",
                before: null,
                after: new
                {
                    key = entry.Key,
                    label = entry.Label,
                    unit = entry.Unit,
                    value = entry.Value,
                    target = entry.Target,
                    default_target = entry.DefaultTarget,
                    better = entry.Better,
                    window_days = snapshot.Days,
                    window_from = snapshot.From,
                },
                ct: ct);
        }

        _logger.LogWarning(
            "slo.sweep.breached count={Count} keys={Keys} window_days={Days}",
            breaches.Count, string.Join(",", breaches.Select(b => b.Key)), snapshot.Days);
        trace.Event(TraceEvent.CategorySystem, "system.slo.sweep",
            new { window_days = snapshot.Days, breaches = breaches.Count,
                  keys = breaches.Select(b => b.Key).ToArray() },
            error: $"{breaches.Count} objective(s) missed");
    }
}
