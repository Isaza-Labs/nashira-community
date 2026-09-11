using nashira_backend.Services.Settings;

namespace nashira_backend.BackgroundServices;

// Seeds the settings catalog at boot, then keeps the snapshot fresh.
//
// The refresh is not about the replica that took the write — that one refreshes itself
// on the way out of the request. It is about every other replica, which would otherwise
// keep serving the old value until it restarted. Without this, "I turned it off and it
// is still on" would be true and unexplainable.
public sealed class AppSettingsRefreshHostedService : BackgroundService
{
    private readonly AppSettingsProvider _settings;
    private readonly ILogger<AppSettingsRefreshHostedService> _logger;

    public AppSettingsRefreshHostedService(
        AppSettingsProvider settings, ILogger<AppSettingsRefreshHostedService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _settings.SeedAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Seeding is metadata for the screen. Failing it must not stop the platform:
            // every read still falls back to configuration, which is where the values
            // lived before this existed.
            _logger.LogError(ex, "settings.seed.failed — the screen may be missing rows");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(AppSettingsProvider.RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }

            await _settings.RefreshAsync(stoppingToken);
        }
    }
}
