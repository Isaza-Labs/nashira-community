using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Email;
using ChannelEntity = nashira_backend.Data.Models.EmailChannel;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Shared plumbing for the mailbox tools (list_emails, read_email, …), the same
// way DocumentText backs the report tools. Not a handler itself.
//
// Channel resolution mirrors EmailChannelFallback's stance: named channel wins,
// otherwise the oldest enabled channel that has IMAP configured — deterministic,
// so a triage conversation does not hop mailboxes when a new channel appears.
internal static class MailboxToolSupport
{
    public static async Task<(ChannelEntity? Channel, string? Error)> ResolveChannelAsync(
        AppDbContext db, JsonElement args, CancellationToken ct)
    {
        var reference = Str(args, "channel");
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var channel = Guid.TryParse(reference, out var id)
                ? await db.EmailChannels.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.IsActive && c.Enabled && c.EmailChannelId == id, ct)
                : await db.EmailChannels.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.IsActive && c.Enabled && c.Slug == reference, ct);
            return channel is null
                ? (null, $"email channel '{reference}' not found or disabled — check /admin/email")
                : (channel, null);
        }

        var fallback = await db.EmailChannels.AsNoTracking()
            .Where(c => c.IsActive && c.Enabled && c.ImapHost != null && c.ImapHost != "")
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Name)
            .FirstOrDefaultAsync(ct);
        return fallback is null
            ? (null, "no email channel has IMAP configured — add an IMAP host to a channel in /admin/email")
            : (fallback, null);
    }

    public static (List<uint> Uids, string? Error) ReadUids(JsonElement args)
    {
        if (!args.TryGetProperty("uids", out var v) || v.ValueKind != JsonValueKind.Array)
            return ([], "`uids` (array of message uids from list_emails) is required");
        var uids = new List<uint>();
        foreach (var el in v.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetUInt32(out var uid))
                return ([], "every entry of `uids` must be a positive integer");
            uids.Add(uid);
        }
        return uids.Count == 0 ? ([], "`uids` must not be empty") : (uids, null);
    }

    public static object Project(EmailSummary s) => new
    {
        uid = s.Uid,
        folder = s.Folder,
        from = s.From,
        subject = s.Subject,
        date = s.Date,
        seen = s.Seen,
        flagged = s.Flagged,
        has_attachments = s.HasAttachments,
    };

    public static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(JsonElement a, string k, bool def) =>
        a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : def;

    public static int Int(JsonElement a, string k, int def) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : def;

    public static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
