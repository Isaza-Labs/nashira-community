using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using nashira_backend.Data.Db;
using nashira_backend.Services.Messaging;

namespace nashira_backend.BackgroundServices;

// Caps the messaging audit tables and clears spent link tokens.
//
// Inbound events and deliveries are append-only and one busy Slack workspace
// writes a row per message, so without a sweep they grow without bound — the same
// reason trace_events and git webhook deliveries have one.
//
// Expired link tokens are deleted rather than kept: they are single-use and
// short-lived, and a consumed token has no forensic value that the identity link
// itself does not already carry.
public sealed class MessagingRetentionHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopes;
    private readonly MessagingOptions _options;
    private readonly ILogger<MessagingRetentionHostedService> _logger;

    public MessagingRetentionHostedService(
        IServiceScopeFactory scopes, IOptions<MessagingOptions> options,
        ILogger<MessagingRetentionHostedService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_options.RetentionDays <= 0)
        {
            _logger.LogInformation("messaging.retention.disabled");
            return;
        }

        // Let the app finish starting before the first sweep; nothing here is urgent.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await SweepAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "messaging.retention.failed"); }
        }
        while (await SafeWaitAsync(timer, ct));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
        var now = DateTime.UtcNow;

        // ExecuteDelete, not a load-then-remove: these tables can hold hundreds of
        // thousands of rows and materialising them to delete them is what turns a
        // retention sweep into an outage.
        var inbound = await db.MessagingInboundEvents.Where(e => e.At < cutoff).ExecuteDeleteAsync(ct);
        var deliveries = await db.MessagingDeliveries.Where(d => d.At < cutoff).ExecuteDeleteAsync(ct);
        var tokens = await db.MessagingLinkTokens
            .Where(t => t.ExpiresAt < now || t.ConsumedAt != null)
            .ExecuteDeleteAsync(ct);

        if (inbound + deliveries + tokens > 0)
            _logger.LogInformation(
                "messaging.retention.swept inbound={Inbound} deliveries={Deliveries} tokens={Tokens} days={Days}",
                inbound, deliveries, tokens, _options.RetentionDays);
    }
}
