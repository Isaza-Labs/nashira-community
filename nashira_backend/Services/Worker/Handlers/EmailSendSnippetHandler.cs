using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Email;
using nashira_backend.Services.Workflow;
using ChannelEntity = nashira_backend.Data.Models.EmailChannel;

namespace nashira_backend.Services.Worker.Handlers;

// Native SMTP snippet — FlowWeaver's `email_send`, on Nashira's channel model.
//
// `channel` names an EmailChannel row (by slug or id); omitted, the message goes
// through the deployment's configured relay (EmailService), which is exactly the
// fallback the channel model documents. The two paths are not equally capable —
// the config relay does one attachment and no bcc — and the handler says so
// instead of quietly dropping fields.
//
// Input (the node's config_overrides, resolved before dispatch):
//   {
//     "channel": "ops-relay" | "<guid>",       // optional; omit for the default relay
//     "to": "a@x.com" | ["a@x.com", …],        // to/cc/bcc: string, csv string or array
//     "cc": [...], "bcc": [...],
//     "subject": "…",
//     "body": "plain text",                    // body and/or html; at least one
//     "html": "<p>…</p>",
//     "reply_to": "…",
//     "attachments": [{ "file_name": "r.pdf", "content_base64": "…",
//                       "content_type": "application/pdf" }]
//   }
//
// A typical pairing: a `report` step upstream, `{{ steps.report.output.base64 }}`
// as the attachment content here.
public sealed class EmailSendSnippetHandler : ISnippetHandler
{
    private readonly AppDbContext _db;
    private readonly IEmailChannelSender _channelSender;
    private readonly IEmailService _defaultRelay;
    private readonly ILogger<EmailSendSnippetHandler> _logger;

    public EmailSendSnippetHandler(
        AppDbContext db, IEmailChannelSender channelSender, IEmailService defaultRelay,
        ILogger<EmailSendSnippetHandler> logger)
    {
        _db = db;
        _channelSender = channelSender;
        _defaultRelay = defaultRelay;
        _logger = logger;
    }

    public string Type => Data.Models.Snippet.TypeEmailSend;

    // A delivered email cannot be recalled. The rollback planner refuses to promise
    // otherwise — same tier as FlowWeaver gives this type.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.NonReversible;

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var src = request.Input;

        var subject = Str(src, "subject");
        if (string.IsNullOrWhiteSpace(subject))
            return SnippetResult.Fail("email_send needs a `subject`", "bad_input");

        var body = Str(src, "body");
        var html = Str(src, "html");
        if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(html))
            return SnippetResult.Fail("email_send needs `body` or `html` (or both)", "bad_input");

        IReadOnlyList<string> to, cc, bcc;
        IReadOnlyList<EmailAttachment> attachments;
        try
        {
            to = Addresses(src, "to");
            cc = Addresses(src, "cc");
            bcc = Addresses(src, "bcc");
            attachments = Attachments(src);
        }
        catch (ArgumentException ex)
        {
            return SnippetResult.Fail(ex.Message, "bad_input");
        }

        // Channel path: the named relay, with the full message shape.
        var channelRef = Str(src, "channel");
        if (!string.IsNullOrWhiteSpace(channelRef))
        {
            var channel = await FindChannelAsync(channelRef!, ct);
            if (channel is null)
                return SnippetResult.Fail(
                    $"email channel '{channelRef}' not found or disabled — check /admin/email", "not_found");

            // A channel may carry its own default recipients for exactly this case.
            if (to.Count + cc.Count + bcc.Count == 0)
            {
                to = SplitCsv(channel.DefaultRecipients);
                if (to.Count == 0)
                    return SnippetResult.Fail(
                        "no recipients: give `to` (or cc/bcc), or set default recipients on the channel",
                        "bad_input");
            }

            var started = DateTime.UtcNow;
            try
            {
                await _channelSender.SendAsync(channel, new EmailChannelMessage
                {
                    To = to,
                    Cc = cc,
                    Bcc = bcc,
                    Subject = subject!,
                    TextBody = body,
                    HtmlBody = html,
                    ReplyTo = Str(src, "reply_to"),
                    Attachments = attachments,
                    // A named channel can set the sender per message, so a declared one is
                    // honoured rather than dropped (snippets/SPEC.md `email_send`).
                    FromAddress = Str(src, "from_address"),
                    FromName = Str(src, "from_name"),
                }, ct);
            }
            catch (DomainException ex)
            {
                _logger.LogWarning("workflow.email_send.failed channel={Channel} error={Error}",
                    channel.Name, ex.Message);
                // A relay that refused now may accept in a minute; a bad address will not.
                return SnippetResult.Fail(ex.Message, "send_failed", retryable: true);
            }

            var recipients = to.Count + cc.Count + bcc.Count;
            return SnippetResult.Ok(new
            {
                ok = true,
                channel_id = channel.EmailChannelId,
                channel_name = channel.Name,
                recipients,
                attachments = attachments.Count,
            }, StepChange.Changed, logs: $"SMTP {channel.Host}:{channel.Port} via '{channel.Name}' "
                + $"→ {recipients} recipient(s), {attachments.Count} attachment(s), "
                + $"{(int)(DateTime.UtcNow - started).TotalMilliseconds}ms");
        }

        // Default-relay path (appsettings Smtp:*). Less capable, and honest about it.

        // The deployment relay sends as its own configured address — IEmailService has no
        // sender parameter at all — so a declared sender cannot be honoured here. It is
        // REFUSED rather than dropped: a message that went out from the wrong address is
        // not recoverable by re-running the step, which is why the contract singles this
        // key out (snippets/SPEC.md `email_send`). Naming a channel is the fix, and the
        // message says so.
        if (!string.IsNullOrWhiteSpace(Str(src, "from_address")) || !string.IsNullOrWhiteSpace(Str(src, "from_name")))
            return SnippetResult.Fail(
                "`from_address`/`from_name` need a named channel — the default relay sends as its own "
                + "configured sender and cannot set one per message",
                "not_supported");

        if (bcc.Count > 0)
            return SnippetResult.Fail(
                "`bcc` needs a named channel — the default relay path does not support it", "bad_input");
        if (attachments.Count > 1)
            return SnippetResult.Fail(
                "more than one attachment needs a named channel — the default relay sends at most one",
                "bad_input");
        if (to.Count == 0)
            return SnippetResult.Fail("`to` is required when no channel is named", "bad_input");

        var startedAt = DateTime.UtcNow;
        try
        {
            await _defaultRelay.SendAsync(
                to, subject!,
                html ?? body!,
                isHtml: !string.IsNullOrWhiteSpace(html),
                cc: cc.Count > 0 ? cc : null,
                attachment: attachments.Count == 1 ? attachments[0] : null,
                ct);
        }
        catch (DomainException ex)
        {
            _logger.LogWarning("workflow.email_send.failed channel=default error={Error}", ex.Message);
            return SnippetResult.Fail(ex.Message, "send_failed", retryable: true);
        }

        return SnippetResult.Ok(new
        {
            ok = true,
            channel_name = "default relay",
            recipients = to.Count + cc.Count,
            attachments = attachments.Count,
        }, StepChange.Changed, logs: $"default SMTP relay → {to.Count + cc.Count} recipient(s), "
            + $"{(int)(DateTime.UtcNow - startedAt).TotalMilliseconds}ms");
    }

    private Task<ChannelEntity?> FindChannelAsync(string reference, CancellationToken ct) =>
        Guid.TryParse(reference, out var id)
            ? _db.EmailChannels.FirstOrDefaultAsync(
                c => c.IsActive && c.Enabled && c.EmailChannelId == id, ct)
            : _db.EmailChannels.FirstOrDefaultAsync(
                c => c.IsActive && c.Enabled && c.Slug == reference, ct);

    // A recipient field accepts a bare string, a comma-separated string, or an
    // array — templates that fan out from an upstream step produce arrays, and
    // hand-authored nodes almost always produce one string.
    private static IReadOnlyList<string> Addresses(JsonElement src, string name)
    {
        if (src.ValueKind != JsonValueKind.Object
            || !src.TryGetProperty(name, out var v)
            || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];

        if (v.ValueKind == JsonValueKind.String)
            return SplitCsv(v.GetString());

        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .SelectMany(e => SplitCsv(e.GetString()))
                .ToList();

        throw new ArgumentException($"`{name}` must be a string or an array of strings");
    }

    private static List<string> SplitCsv(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static IReadOnlyList<EmailAttachment> Attachments(JsonElement src)
    {
        if (src.ValueKind != JsonValueKind.Object
            || !src.TryGetProperty("attachments", out var v)
            || v.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<EmailAttachment>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("every entry of `attachments` must be an object");
            var fileName = Str(item, "file_name");
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("every attachment needs a `file_name`");
            var content = Str(item, "content_base64");
            if (string.IsNullOrWhiteSpace(content))
                throw new ArgumentException($"attachment '{fileName}' needs `content_base64`");
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(content!);
            }
            catch (FormatException)
            {
                throw new ArgumentException($"attachment '{fileName}': content_base64 is not valid base64");
            }
            list.Add(new EmailAttachment(
                fileName!, Str(item, "content_type") ?? "application/octet-stream", bytes));
        }
        return list;
    }

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
