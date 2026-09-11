using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using ChannelEntity = nashira_backend.Data.Models.EmailChannel;

namespace nashira_backend.Services.Email;

// The channel system mail (e.g. new-user credentials) falls back to when the
// deployment relay (Smtp:*) is not configured. Many installs never touch
// appsettings and do all their SMTP setup in /admin/email; refusing to use a
// working channel there reads as "email is not configured" on a system where the
// admin just watched a test email arrive.
//
// Selection is deterministic — the oldest enabled channel — so system mail does
// not hop relays when a newer channel appears. Deployments that care which relay
// carries system mail should configure Smtp:*, which always wins.
public interface IEmailChannelFallback
{
    Task<ChannelEntity?> FindAsync(CancellationToken ct);
}

public sealed class EmailChannelFallback : IEmailChannelFallback
{
    private readonly IServiceScopeFactory _scopes;

    public EmailChannelFallback(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<ChannelEntity?> FindAsync(CancellationToken ct)
    {
        // Consumers of this service are singletons; AppDbContext is scoped.
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.EmailChannels.AsNoTracking()
            .Where(c => c.IsActive && c.Enabled)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Name)
            .FirstOrDefaultAsync(ct);
    }
}
