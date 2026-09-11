using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Email;
using nashira_backend.Services.Export;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Sends an email, optionally attaching a previously generated export artifact.
// External side effect → write / single_confirm.
//
// The deployment relay (Smtp:*) is preferred; without it the mail goes through
// the fallback email channel — same policy as the credentials mail on user
// creation, so "the AI can send mail" and "the test button works" agree.
public sealed class SendEmailHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "to":{"type":["array","string"],"items":{"type":"string"},"description":"Recipient address(es)"},
          "subject":{"type":"string"},
          "body":{"type":"string"},
          "is_html":{"type":"boolean","default":false},
          "cc":{"type":"array","items":{"type":"string"}},
          "export_artifact_id":{"type":"string","description":"Attach a generated export (id from export_table)"}
        },"required":["to","subject","body"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IEmailService _email;
    private readonly IEmailChannelFallback _channelFallback;
    private readonly IEmailChannelSender _channelSender;
    private readonly IExportService _export;

    public SendEmailHandler(
        IEmailService email, IEmailChannelFallback channelFallback, IEmailChannelSender channelSender,
        IExportService export)
    {
        _email = email;
        _channelFallback = channelFallback;
        _channelSender = channelSender;
        _export = export;
    }

    public string Name => "send_email";
    public string Description =>
        "Sends an email. Optionally attaches an export artifact by id (from export_table). " +
        "Requires SMTP to be configured (an Smtp:* relay or an email channel).";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var to = ReadRecipients(args, "to");
            if (to.Count == 0) return Err("at least one recipient is required");
            var subject = Str(args, "subject") ?? "";
            var body = Str(args, "body") ?? "";
            var isHtml = Bool(args, "is_html", false);
            var cc = ReadRecipients(args, "cc");

            EmailAttachment? attachment = null;
            if (Str(args, "export_artifact_id") is { } idStr)
            {
                if (!Guid.TryParse(idStr, out var id)) return Err("export_artifact_id must be a uuid");
                var artifact = await _export.GetForDownloadAsync(id, ct);
                if (artifact is null) return Err("export_artifact_id not found");
                attachment = new EmailAttachment(artifact.FileName, artifact.ContentType, artifact.Content);
            }

            if (_email.IsConfigured)
            {
                await _email.SendAsync(to, subject, body, isHtml, cc.Count > 0 ? cc : null, attachment, ct);
                return JsonSerializer.SerializeToElement(new { sent = true, to, subject });
            }

            var channel = await _channelFallback.FindAsync(ct);
            if (channel is null)
                return Err("email is not configured — set the Smtp:* relay or create an enabled " +
                           "email channel in /admin/email");

            await _channelSender.SendAsync(channel, new EmailChannelMessage
            {
                To = to,
                Cc = cc,
                Subject = subject,
                TextBody = isHtml ? null : body,
                HtmlBody = isHtml ? body : null,
                Attachments = attachment is null ? [] : [attachment],
            }, ct);
            return JsonSerializer.SerializeToElement(new { sent = true, to, subject, channel = channel.Name });
        }
        catch (DomainException ex)
        {
            return Err(ex.Message);
        }
    }

    private static List<string> ReadRecipients(JsonElement args, string key)
    {
        var list = new List<string>();
        if (!args.TryGetProperty(key, out var v)) return list;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!.Trim());
        }
        else if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in v.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String && el.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                    list.Add(s.Trim());
        }
        return list;
    }

    private static bool Bool(JsonElement a, string k, bool def) =>
        a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;
    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static JsonElement Err(string m) => JsonSerializer.SerializeToElement(new { error = m });
}
