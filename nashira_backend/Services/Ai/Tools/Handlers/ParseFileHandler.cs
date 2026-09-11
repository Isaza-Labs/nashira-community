using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Files;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Parses a file into structured data. The file arrives one of three ways:
// `attachment` (the filename of a file attached to this conversation — the current
// message's files come from the turn scope, earlier messages' files from the
// persisted conversation_attachments rows), `content` (inline text for
// csv/json/text), or `content_base64`. Read-only.
public sealed class ParseFileHandler : IToolHandler
{
    private const int MaxBytes = 5 * 1024 * 1024;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "format":{"type":"string","enum":["csv","xlsx","json","text","pdf","docx"],"description":"Input format (match the file extension; legacy binary .doc is not supported — ask for .docx or pdf)"},
          "attachment":{"type":"string","description":"Filename of a file attached to this conversation (current or earlier message)"},
          "content":{"type":"string","description":"Text content (csv/json/text)"},
          "content_base64":{"type":"string","description":"Base64 content (alternative to attachment for binary formats)"},
          "sheet":{"type":"string","description":"xlsx only: which worksheet to read. Omit for the first — the result lists the others."},
          "has_header":{"type":"boolean","default":true,"description":"First row is a header (csv/xlsx, and docx tables)"},
          "page":{"type":"integer","description":"pdf only: return just this page (1-based). Omit for all pages."}
        },"required":["format"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IFileParsingService _parser;
    private readonly AgentTurnScope _turnScope;
    private readonly AppDbContext _db;

    public ParseFileHandler(IFileParsingService parser, AgentTurnScope turnScope, AppDbContext db)
    {
        _parser = parser;
        _turnScope = turnScope;
        _db = db;
    }

    public string Name => "parse_file";
    public string Description =>
        "Parses a file into structured data. csv/xlsx return columns + row objects; pdf returns text " +
        "per page (use `page` to fetch one); docx returns body text plus its tables as grids; json " +
        "returns the parsed value; text returns lines. For a file attached to this conversation pass " +
        "its filename as `attachment` (required for binary formats: xlsx, pdf, docx; works for files " +
        "from earlier messages too); otherwise provide `content` or `content_base64`.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var format = (Str(args, "format") ?? "").Trim().ToLowerInvariant();
        var hasHeader = Bool(args, "has_header", true);

        try
        {
            switch (format)
            {
                case "xlsx":
                {
                    var (bytes, err) = await ResolveBytesAsync(args, ct);
                    if (bytes is null) return Err(err!);
                    var table = Parse(bytes, hasHeader, Str(args, "sheet"));
                    return Table("xlsx", table);
                }
                case "csv":
                {
                    var (text, err) = await ResolveTextAsync(args, ct);
                    if (text is null) return Err(err!);
                    var table = _parser.ParseCsv(text, hasHeader);
                    return Table("csv", table);
                }
                case "json":
                {
                    var (text, err) = await ResolveTextAsync(args, ct);
                    if (text is null) return Err(err!);
                    using var doc = JsonDocument.Parse(text);
                    return JsonSerializer.SerializeToElement(new { format = "json", data = doc.RootElement.Clone() });
                }
                case "text":
                {
                    var (text, err) = await ResolveTextAsync(args, ct);
                    if (text is null) return Err(err!);
                    var lines = text.Replace("\r\n", "\n").Split('\n');
                    return JsonSerializer.SerializeToElement(new { format = "text", line_count = lines.Length, lines });
                }
                case "pdf":
                {
                    var (bytes, err) = await ResolveBytesAsync(args, ct);
                    if (bytes is null) return Err(err!);
                    var doc = _parser.ParsePdf(bytes);
                    if (Int(args, "page") is { } p)
                    {
                        if (p < 1 || p > doc.Pages.Count)
                            return Err($"page must be between 1 and {doc.Pages.Count}");
                        return JsonSerializer.SerializeToElement(
                            new { format = "pdf", page = p, page_count = doc.Pages.Count, text = doc.Pages[p - 1] });
                    }
                    return JsonSerializer.SerializeToElement(new
                    {
                        format = "pdf",
                        page_count = doc.Pages.Count,
                        pages = doc.Pages.Select((t, i) => new { page = i + 1, text = t }),
                    });
                }
                case "docx":
                {
                    var (bytes, err) = await ResolveBytesAsync(args, ct);
                    if (bytes is null) return Err(err!);
                    var doc = _parser.ParseDocx(bytes, hasHeader);
                    return JsonSerializer.SerializeToElement(new
                    {
                        format = "docx",
                        text = doc.Pages.FirstOrDefault() ?? string.Empty,
                        table_count = doc.Tables.Count,
                        tables = doc.Tables.Select(t => new { columns = t.Columns, row_count = t.Rows.Count, rows = t.Rows }),
                    });
                }
                default:
                    return Err("format must be one of csv, xlsx, json, text, pdf, docx");
            }
        }
        catch (JsonException)
        {
            return Err("content is not valid JSON");
        }
        catch (Exception ex)
        {
            return Err($"failed to parse {format}: {ex.Message}");
        }
    }

    private ParsedTable Parse(byte[] bytes, bool hasHeader, string? sheet) =>
        _parser.ParseXlsx(bytes, hasHeader, sheet);

    private static JsonElement Table(string format, ParsedTable t) =>
        JsonSerializer.SerializeToElement(new { format, sheet = t.Sheet, sheet_names = t.SheetNames, columns = t.Columns, row_count = t.Rows.Count, rows = t.Rows });

    private async Task<(string? Text, string? Error)> ResolveTextAsync(JsonElement args, CancellationToken ct)
    {
        var text = Str(args, "content") ?? "";
        if (text.Length == 0 && (HasProp(args, "attachment") || HasProp(args, "content_base64")))
        {
            var (bytes, err) = await ResolveBytesAsync(args, ct);
            if (bytes is null) return (null, err);
            text = Encoding.UTF8.GetString(bytes);
        }
        if (text.Length == 0) return (null, "content is required");
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) return (null, "content exceeds 5 MiB");
        return (text, null);
    }

    private async Task<(byte[]? Bytes, string? Error)> ResolveBytesAsync(JsonElement args, CancellationToken ct)
    {
        // A named attachment wins: it is the raw bytes the user actually sent, with
        // no base64 round-trip for the model to mangle. The current message's files
        // sit on the turn scope; earlier messages' files were persisted per
        // conversation by the runner.
        if (Str(args, "attachment") is { Length: > 0 } rawName)
        {
            var name = rawName.Trim();
            if (_turnScope.TryGetAttachment(name, out var scoped))
                return scoped.Length > MaxBytes ? (null, "attachment exceeds 5 MiB") : (scoped, null);

            // Names first, the one matching row's bytes second — never every row's
            // content (a conversation can hold up to 20 × 5 MiB).
            var persistedNames = new List<string>();
            if (_turnScope.ConversationId is { } convId)
            {
                persistedNames = await _db.ConversationAttachments.AsNoTracking()
                    .Where(a => a.ConversationId == convId && a.IsActive)
                    .Select(a => a.Filename)
                    .ToListAsync(ct);
                var match = persistedNames.FirstOrDefault(
                    f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    var content = await _db.ConversationAttachments.AsNoTracking()
                        .Where(a => a.ConversationId == convId && a.Filename == match && a.IsActive)
                        .Select(a => a.Content)
                        .FirstAsync(ct);
                    return content.Length > MaxBytes ? (null, "attachment exceeds 5 MiB") : (content, null);
                }
            }

            var available = _turnScope.AttachmentNames
                .Concat(persistedNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return available.Count == 0
                ? (null, $"no attachment named \"{name}\" — this conversation has no attachments; ask the user to attach the file to their next message")
                : (null, $"no attachment named \"{name}\" — this conversation's attachments: {string.Join(", ", available)}");
        }

        var b64 = Str(args, "content_base64");
        if (string.IsNullOrEmpty(b64)) return (null, "attachment or content_base64 is required");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { return (null, "content_base64 is not valid base64"); }
        return bytes.Length > MaxBytes ? (null, "content exceeds 5 MiB") : (bytes, null);
    }

    private static bool HasProp(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 };
    private static int? Int(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static bool Bool(JsonElement a, string k, bool def) =>
        a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;
    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static JsonElement Err(string m) => JsonSerializer.SerializeToElement(new { error = m });
}
