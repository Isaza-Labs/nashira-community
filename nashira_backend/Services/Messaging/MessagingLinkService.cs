using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Messaging;

public sealed record LinkInvite(string Cleartext, string Url);

public sealed record LinkPreview(
    string Provider, string ChannelName, string ExternalUserId, DateTime ExpiresAt);

// Self-service account linking.
//
// CreateLinkAsync mints a one-time token, called by the ingest path when an
// unlinked user messages the bot. Preview and Confirm are the authenticated
// endpoints the user hits from the link the bot sent them. Confirm binds the
// external identity to the *authenticated* caller — never to anything the message
// claimed — and consumes the token atomically, so it can never be replayed.
public interface IMessagingLinkService
{
    Task<LinkInvite> CreateLinkAsync(
        MessagingChannel channel, string? externalWorkspaceId, string externalUserId, CancellationToken ct);

    Task<LinkPreview> PreviewAsync(string token, CancellationToken ct);

    Task<Guid> ConfirmAsync(string token, CancellationToken ct);
}

public sealed class MessagingLinkService : IMessagingLinkService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;
    private readonly MessagingOptions _options;

    public MessagingLinkService(
        AppDbContext db, ICurrentUser caller, IOptions<MessagingOptions> options)
    {
        _db = db;
        _caller = caller;
        _options = options.Value;
    }

    public async Task<LinkInvite> CreateLinkAsync(
        MessagingChannel channel, string? externalWorkspaceId, string externalUserId, CancellationToken ct)
    {
        var cleartext = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        var ttl = _options.LinkTokenTtlMinutes <= 0 ? 15 : _options.LinkTokenTtlMinutes;

        _db.MessagingLinkTokens.Add(new MessagingLinkToken
        {
            MessagingLinkTokenId = Guid.NewGuid(),
            MessagingChannelId = channel.MessagingChannelId,
            ExternalWorkspaceId = externalWorkspaceId ?? string.Empty,
            ExternalUserId = externalUserId,
            TokenHash = HashToken(cleartext),
            ExpiresAt = now.AddMinutes(ttl),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(ct);

        // Without a configured public base URL the best we could build is a
        // relative "/link?token=…", which is not something a user can click in a
        // chat client. Return an empty Url so the caller degrades to a "link from
        // the web app" message rather than sending a broken link.
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        var url = string.IsNullOrEmpty(baseUrl) ? string.Empty : $"{baseUrl}/link?token={cleartext}";
        return new LinkInvite(cleartext, url);
    }

    public async Task<LinkPreview> PreviewAsync(string token, CancellationToken ct)
    {
        var (tok, channel) = await ResolveAsync(token, ct);
        return new LinkPreview(channel.Provider, channel.Name, tok.ExternalUserId, tok.ExpiresAt);
    }

    public async Task<Guid> ConfirmAsync(string token, CancellationToken ct)
    {
        var (tok, _) = await ResolveAsync(token, ct);
        var now = DateTime.UtcNow;

        // Single-use, enforced atomically BEFORE the link is created: two concurrent
        // confirms of the same token cannot both proceed, and the loser sees the
        // token as already gone rather than silently re-pointing the identity.
        var consumed = await _db.MessagingLinkTokens
            .Where(t => t.MessagingLinkTokenId == tok.MessagingLinkTokenId && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.ConsumedAt, now)
                .SetProperty(t => t.ConsumedByUserId, _caller.UserId)
                .SetProperty(t => t.UpdatedAt, now), ct);

        if (consumed == 0) throw new NotFoundException("link token invalid or expired");

        var workspace = tok.ExternalWorkspaceId ?? string.Empty;

        // Re-link when the external identity already maps to someone, rather than
        // inserting a second row: the unique (channel, workspace, user) index would
        // refuse it, and a person changing which account they use is a normal thing
        // to do. Re-linking keeps the conversation history attached to the thread.
        var existing = await _db.MessagingIdentityLinks.FirstOrDefaultAsync(
            l => l.MessagingChannelId == tok.MessagingChannelId
                 && l.ExternalWorkspaceId == workspace
                 && l.ExternalUserId == tok.ExternalUserId, ct);

        Guid linkId;
        if (existing is not null)
        {
            existing.LinkedUserId = _caller.UserId;
            existing.IsActive = true;
            existing.UpdatedAt = now;
            linkId = existing.MessagingIdentityLinkId;
        }
        else
        {
            var link = new MessagingIdentityLink
            {
                MessagingIdentityLinkId = Guid.NewGuid(),
                MessagingChannelId = tok.MessagingChannelId,
                ExternalWorkspaceId = workspace,
                ExternalUserId = tok.ExternalUserId,
                LinkedUserId = _caller.UserId,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.MessagingIdentityLinks.Add(link);
            linkId = link.MessagingIdentityLinkId;
        }

        await _db.SaveChangesAsync(ct);
        return linkId;
    }

    // Resolves an active token by hash. A wrong, consumed or expired token looks
    // identical to a missing one, so probing cannot enumerate which tokens exist.
    private async Task<(MessagingLinkToken Token, MessagingChannel Channel)> ResolveAsync(
        string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new NotFoundException("link token invalid or expired");

        var hash = HashToken(token);
        var now = DateTime.UtcNow;

        var tok = await _db.MessagingLinkTokens.FirstOrDefaultAsync(
            t => t.TokenHash == hash && t.ConsumedAt == null && t.ExpiresAt > now && t.IsActive, ct);
        if (tok is null) throw new NotFoundException("link token invalid or expired");

        var channel = await _db.MessagingChannels.AsNoTracking().FirstOrDefaultAsync(
            c => c.MessagingChannelId == tok.MessagingChannelId && c.IsActive, ct);
        if (channel is null) throw new NotFoundException("messaging channel not found");

        return (tok, channel);
    }

    private static byte[] HashToken(string cleartext) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(cleartext));
}
