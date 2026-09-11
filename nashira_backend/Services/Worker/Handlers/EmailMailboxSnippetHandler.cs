using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Email;
using nashira_backend.Services.Workflow;
using ChannelEntity = nashira_backend.Data.Models.EmailChannel;

namespace nashira_backend.Services.Worker.Handlers;

// Mailbox (IMAP) snippet — the inbound counterpart of email_send, on the same
// EmailChannel rows. One handler, several actions, because a mail-triage
// workflow is a sequence of them (list → read → move/delete) and a type per
// action would multiply catalogue entries that always travel together.
//
// Input (the node's config_overrides, resolved before dispatch):
//   {
//     "action": "list" | "read" | "mark" | "move" | "archive" | "delete" | "folders",
//     "channel": "support-mail" | "<guid>",   // optional; omitted, the oldest
//                                             // enabled channel with IMAP is used
//     "folder": "INBOX",                      // optional everywhere
//     // list:    "unread_only", "from_contains", "subject_contains",
//     //          "text_contains", "since_days", "limit"
//     // read:    "uid"
//     // mark:    "uids", "seen" (default true)
//     // move:    "uids", "target_folder"
//     // archive: "uids"
//     // delete:  "uids", "permanent" (default false → server Trash)
//   }
//
// A typical pairing: a `list` step with `unread_only`, a transform that picks
// uids, then `{{ steps.pick.output.uids }}` into an archive or delete step.
public sealed class EmailMailboxSnippetHandler : ISnippetHandler
{
    private readonly AppDbContext _db;
    private readonly IEmailChannelMailbox _mailbox;
    private readonly ILogger<EmailMailboxSnippetHandler> _logger;

    public EmailMailboxSnippetHandler(
        AppDbContext db, IEmailChannelMailbox mailbox, ILogger<EmailMailboxSnippetHandler> logger)
    {
        _db = db;
        _mailbox = mailbox;
        _logger = logger;
    }

    public string Type => Data.Models.Snippet.TypeEmailMailbox;

    // The actions span pure reads to expunge. RequiresCompensation is the honest
    // middle: a mark can be unmarked and a move moved back, and an author whose
    // node only lists can lower it, while one that deletes with `permanent`
    // should raise it to non_reversible — expunge has no compensation.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.RequiresCompensation;

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var src = request.Input;
        var action = Str(src, "action")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(action))
            return SnippetResult.Fail(
                "email_mailbox needs an `action` (list, read, mark, move, archive, delete, folders)",
                "bad_input");

        var channel = await FindChannelAsync(Str(src, "channel"), ct);
        if (channel is null)
            return SnippetResult.Fail(
                Str(src, "channel") is { } named
                    ? $"email channel '{named}' not found or disabled — check /admin/email"
                    : "no email channel has IMAP configured — add an IMAP host to a channel in /admin/email",
                "not_found");

        var folder = Str(src, "folder");
        var started = DateTime.UtcNow;
        try
        {
            return action switch
            {
                "folders" => await FoldersAsync(channel, started, ct),
                "list" => await ListAsync(channel, src, folder, started, ct),
                "read" => await ReadAsync(channel, src, folder, started, ct),
                "mark" => await MarkAsync(channel, src, folder, started, ct),
                "move" => await MoveAsync(channel, src, folder, started, ct),
                "archive" => await ArchiveAsync(channel, src, folder, started, ct),
                "delete" => await DeleteAsync(channel, src, folder, started, ct),
                _ => SnippetResult.Fail(
                    $"unknown action '{action}' — use list, read, mark, move, archive, delete or folders",
                    "bad_input"),
            };
        }
        catch (DomainException ex)
        {
            _logger.LogWarning("workflow.email_mailbox.failed channel={Channel} action={Action} error={Error}",
                channel.Name, action, ex.Message);
            // A mailbox that refused now may answer in a minute; bad input will not,
            // but bad input surfaces as bad_input above, not here.
            return SnippetResult.Fail(ex.Message, "mailbox_failed", retryable: true);
        }
    }

    private async Task<SnippetResult> FoldersAsync(ChannelEntity channel, DateTime started, CancellationToken ct)
    {
        var folders = await _mailbox.ListFoldersAsync(channel, ct);
        return SnippetResult.Ok(new
        {
            ok = true,
            channel_name = channel.Name,
            count = folders.Count,
            folders = folders.Select(f => new { name = f.Name, messages = f.Count, unread = f.Unread }),
        }, StepChange.Unchanged, logs: Log(channel, $"{folders.Count} folder(s)", started));
    }

    private async Task<SnippetResult> ListAsync(
        ChannelEntity channel, JsonElement src, string? folder, DateTime started, CancellationToken ct)
    {
        var sinceDays = Int(src, "since_days", 0);
        var messages = await _mailbox.ListAsync(channel, new EmailMailboxQuery
        {
            Folder = folder,
            UnreadOnly = Bool(src, "unread_only", false),
            FromContains = Str(src, "from_contains"),
            SubjectContains = Str(src, "subject_contains"),
            TextContains = Str(src, "text_contains"),
            Since = sinceDays > 0 ? DateTimeOffset.UtcNow.AddDays(-sinceDays) : null,
            Limit = Math.Clamp(Int(src, "limit", 20), 1, 100),
        }, ct);

        return SnippetResult.Ok(new
        {
            ok = true,
            channel_name = channel.Name,
            folder = folder ?? "INBOX",
            count = messages.Count,
            // `uids` as a flat array so a downstream mark/archive/delete step can
            // take `{{ steps.list.output.uids }}` without a transform in between.
            uids = messages.Select(m => m.Uid),
            messages = messages.Select(m => new
            {
                uid = m.Uid,
                from = m.From,
                subject = m.Subject,
                date = m.Date,
                seen = m.Seen,
                flagged = m.Flagged,
                has_attachments = m.HasAttachments,
            }),
        }, StepChange.Unchanged, logs: Log(channel, $"{messages.Count} message(s)", started));
    }

    private async Task<SnippetResult> ReadAsync(
        ChannelEntity channel, JsonElement src, string? folder, DateTime started, CancellationToken ct)
    {
        if (!TryUid(src, "uid", out var uid))
            return SnippetResult.Fail("`read` needs a `uid` (integer from a list step)", "bad_input");

        var message = await _mailbox.GetAsync(channel, folder, uid, ct);
        if (message is null)
            return SnippetResult.Fail($"message uid {uid} not found in '{folder ?? "INBOX"}'", "not_found");

        return SnippetResult.Ok(new
        {
            ok = true,
            channel_name = channel.Name,
            uid = message.Uid,
            folder = message.Folder,
            from = message.From,
            to = message.To,
            cc = message.Cc,
            reply_to = message.ReplyTo,
            subject = message.Subject,
            date = message.Date,
            seen = message.Seen,
            text_body = message.TextBody,
            html_body = message.HtmlBody,
            body_truncated = message.BodyTruncated,
            attachments = message.Attachments.Select(a => new
            {
                file_name = a.FileName,
                content_type = a.ContentType,
                size_bytes = a.Size,
            }),
        }, StepChange.Unchanged, logs: Log(channel, $"read uid {uid}", started));
    }

    private async Task<SnippetResult> MarkAsync(
        ChannelEntity channel, JsonElement src, string? folder, DateTime started, CancellationToken ct)
    {
        if (!TryUids(src, out var uids, out var error))
            return SnippetResult.Fail(error, "bad_input");
        var seen = Bool(src, "seen", true);
        var affected = await _mailbox.MarkAsync(channel, folder, uids, seen, ct);
        return SnippetResult.Ok(
            new { ok = true, channel_name = channel.Name, marked = affected, requested = uids.Count, seen },
            affected > 0 ? StepChange.Changed : StepChange.Unchanged,
            logs: Log(channel, $"marked {affected}/{uids.Count} {(seen ? "seen" : "unseen")}", started));
    }

    private async Task<SnippetResult> MoveAsync(
        ChannelEntity channel, JsonElement src, string? folder, DateTime started, CancellationToken ct)
    {
        if (!TryUids(src, out var uids, out var error))
            return SnippetResult.Fail(error, "bad_input");
        var target = Str(src, "target_folder");
        if (string.IsNullOrWhiteSpace(target))
            return SnippetResult.Fail("`move` needs a `target_folder`", "bad_input");
        var moved = await _mailbox.MoveAsync(channel, folder, uids, target!, ct);
        return SnippetResult.Ok(
            new { ok = true, channel_name = channel.Name, moved, requested = uids.Count, target_folder = target },
            moved > 0 ? StepChange.Changed : StepChange.Unchanged,
            logs: Log(channel, $"moved {moved}/{uids.Count} → {target}", started));
    }

    private async Task<SnippetResult> ArchiveAsync(
        ChannelEntity channel, JsonElement src, string? folder, DateTime started, CancellationToken ct)
    {
        if (!TryUids(src, out var uids, out var error))
            return SnippetResult.Fail(error, "bad_input");
        var (moved, archiveFolder) = await _mailbox.ArchiveAsync(channel, folder, uids, ct);
        return SnippetResult.Ok(
            new
            {
                ok = true, channel_name = channel.Name, archived = moved, requested = uids.Count,
                archive_folder = archiveFolder,
            },
            moved > 0 ? StepChange.Changed : StepChange.Unchanged,
            logs: Log(channel, $"archived {moved}/{uids.Count} → {archiveFolder}", started));
    }

    private async Task<SnippetResult> DeleteAsync(
        ChannelEntity channel, JsonElement src, string? folder, DateTime started, CancellationToken ct)
    {
        if (!TryUids(src, out var uids, out var error))
            return SnippetResult.Fail(error, "bad_input");
        var permanent = Bool(src, "permanent", false);
        var (affected, outcome) = await _mailbox.DeleteAsync(channel, folder, uids, permanent, ct);
        return SnippetResult.Ok(
            new { ok = true, channel_name = channel.Name, deleted = affected, requested = uids.Count, outcome },
            affected > 0 ? StepChange.Changed : StepChange.Unchanged,
            logs: Log(channel, $"deleted {affected}/{uids.Count} ({outcome})", started));
    }

    // Named channel by slug/id; otherwise the oldest enabled channel that has an
    // IMAP host — the deterministic pick EmailChannelFallback established for SMTP.
    private async Task<ChannelEntity?> FindChannelAsync(string? reference, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(reference))
            return Guid.TryParse(reference, out var id)
                ? await _db.EmailChannels.FirstOrDefaultAsync(
                    c => c.IsActive && c.Enabled && c.EmailChannelId == id, ct)
                : await _db.EmailChannels.FirstOrDefaultAsync(
                    c => c.IsActive && c.Enabled && c.Slug == reference, ct);

        return await _db.EmailChannels
            .Where(c => c.IsActive && c.Enabled && c.ImapHost != null && c.ImapHost != "")
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Name)
            .FirstOrDefaultAsync(ct);
    }

    private static string Log(ChannelEntity channel, string what, DateTime started) =>
        $"IMAP {channel.ImapHost}:{channel.ImapPort} via '{channel.Name}' → {what}, "
        + $"{(int)(DateTime.UtcNow - started).TotalMilliseconds}ms";

    private static bool TryUids(JsonElement src, out List<uint> uids, out string error)
    {
        uids = [];
        error = string.Empty;
        // A single `uid` is accepted as a courtesy — hand-authored nodes often
        // operate on the one message a read step just looked at.
        if (TryUid(src, "uid", out var single)) uids.Add(single);
        if (src.ValueKind == JsonValueKind.Object && src.TryGetProperty("uids", out var v))
        {
            if (v.ValueKind != JsonValueKind.Array)
            {
                error = "`uids` must be an array of integers";
                return false;
            }
            foreach (var el in v.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Number || !el.TryGetUInt32(out var uid))
                {
                    error = "every entry of `uids` must be a positive integer";
                    return false;
                }
                uids.Add(uid);
            }
        }
        if (uids.Count == 0)
        {
            error = "give `uids` (array) or `uid` (integer) from a list step";
            return false;
        }
        uids = uids.Distinct().ToList();
        return true;
    }

    private static bool TryUid(JsonElement src, string key, out uint uid)
    {
        uid = 0;
        return src.ValueKind == JsonValueKind.Object && src.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out uid);
    }

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool Bool(JsonElement e, string k, bool def) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : def;

    private static int Int(JsonElement e, string k, int def) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : def;
}
