using System.Text;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Whether a stored artifact's bytes can be handed back to the model as text.
//
// Reports and exports share one table shape and two completely different fates: a
// markdown report is the document itself and can be read back verbatim, while a PDF
// or an XLSX is a rendering whose bytes decode into mojibake. Reading one through the
// generic REST path did exactly that — `execute_operation` on a download endpoint
// UTF-8-decodes whatever came back, so a PDF arrived as a page of replacement
// characters that the agent then tried to quote.
//
// So the question is answered once, here, and both list_reports and read_report use
// the same answer: `readable` in a listing means read_report will return prose.
internal static class DocumentText
{
    // Substrings rather than an exact set: content types carry charsets
    // ("text/markdown; charset=utf-8") and structured suffixes ("application/vnd.x+json").
    private static readonly string[] TextualMarkers =
    [
        "text/", "application/json", "application/xml", "application/yaml",
        "application/x-yaml", "application/javascript", "+json", "+xml", "+yaml",
    ];

    // 8 KB is what `file(1)` and git both sample. A NUL in that window means bytes, not
    // prose — it catches a PDF mislabelled as text/plain, which the content type alone
    // never would.
    private const int SniffBytes = 8 * 1024;

    public static bool IsTextualContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var ct = contentType.Trim().ToLowerInvariant();
        return TextualMarkers.Any(m => ct.Contains(m, StringComparison.Ordinal));
    }

    public static bool LooksBinary(byte[] content)
    {
        var end = Math.Min(content.Length, SniffBytes);
        for (var i = 0; i < end; i++)
            if (content[i] == 0)
                return true;
        return false;
    }

    // True when the row can be read back as prose: the declared type says text AND the
    // bytes agree. Both have to hold — a label is a claim, and the sniff is the check.
    public static bool IsReadable(string? contentType, byte[] content) =>
        IsTextualContentType(contentType) && !LooksBinary(content);

    // Decodes and drops a UTF-8 BOM. Exports written by ExportService use a
    // BOM-less encoder, but a report POSTed by a workflow or pasted by a user can
    // carry one, and a stray U+FEFF at the head of a markdown document breaks the
    // very first heading.
    public static string Decode(byte[] content)
    {
        var text = new UTF8Encoding(false).GetString(content);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    // What to tell the agent instead of handing it bytes. Names the alternative,
    // because "not readable" on its own reads as a defect rather than as a property
    // of the format the agent itself chose.
    public static string BinaryExplanation(string fileName, string contentType) =>
        $"'{fileName}' is {contentType} — a rendered file, so its text cannot be read back. "
        + "Hand the user the download link instead. To be able to re-read a document later, "
        + "keep its markdown with save_report, or generate it with format 'markdown'.";
}
