using System.Text;

namespace nashira_backend.Services.Export;

// The HTML twin of DocumentPdfBuilder: same block list, same palette, self-contained
// (styles inlined) so the file opens from disk or pastes into an email body without
// fetching anything. Everything the model wrote is escaped — a report is a document,
// never a place where markup from a tool result gets to execute.
internal static class DocumentHtmlBuilder
{
    public static string Build(string title, IReadOnlyList<MdBlock> blocks, DateTime generatedAtUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>{Escape(title)}</title>");
        sb.AppendLine(Style());
        sb.AppendLine("</head><body>");
        sb.AppendLine("<main>");
        sb.AppendLine($"<h1 class=\"doc-title\">{Escape(title)}</h1>");
        sb.AppendLine($"<p class=\"doc-meta\">Generated {generatedAtUtc:yyyy-MM-dd HH:mm} UTC</p>");

        foreach (var block in blocks) AppendBlock(sb, block);

        sb.AppendLine("</main></body></html>");
        return sb.ToString();
    }

    private static void AppendBlock(StringBuilder sb, MdBlock block)
    {
        switch (block)
        {
            case MdHeading heading:
                // The document title is already an h1, so the body scale starts at h2 and
                // the outline stays legible to a screen reader.
                var level = Math.Min(heading.Level + 1, 6);
                sb.AppendLine($"<h{level}>{Inline(heading.Inline)}</h{level}>");
                break;

            case MdParagraph paragraph:
                sb.AppendLine($"<p>{Inline(paragraph.Inline)}</p>");
                break;

            case MdList list:
                var tag = list.Ordered ? "ol" : "ul";
                sb.AppendLine($"<{tag}>");
                foreach (var item in list.Items)
                    sb.AppendLine($"<li class=\"depth-{item.Depth}\">{Inline(item.Inline)}</li>");
                sb.AppendLine($"</{tag}>");
                break;

            case MdTable table:
                sb.AppendLine("<div class=\"table-scroll\"><table><thead><tr>");
                foreach (var header in table.Headers) sb.Append("<th>").Append(Inline(header)).Append("</th>");
                sb.AppendLine("</tr></thead><tbody>");
                foreach (var row in table.Rows)
                {
                    sb.Append("<tr>");
                    foreach (var cell in row) sb.Append("<td>").Append(Inline(cell)).Append("</td>");
                    sb.AppendLine("</tr>");
                }
                sb.AppendLine("</tbody></table></div>");
                break;

            case MdCode code:
                var language = code.Language is null ? string.Empty : $" data-language=\"{Escape(code.Language)}\"";
                sb.AppendLine($"<pre{language}><code>{Escape(code.Text)}</code></pre>");
                break;

            case MdQuote quote:
                sb.AppendLine($"<blockquote>{Inline(quote.Inline)}</blockquote>");
                break;

            case MdRule:
                sb.AppendLine("<hr>");
                break;
        }
    }

    private static string Inline(IReadOnlyList<MdSpan> spans)
    {
        var sb = new StringBuilder();
        foreach (var span in spans)
        {
            var text = Escape(span.Text).Replace("\n", "<br>");
            if (span.Code) text = $"<code>{text}</code>";
            if (span.Bold) text = $"<strong>{text}</strong>";
            if (span.Italic) text = $"<em>{text}</em>";
            // Only http(s) hrefs reach this point (MarkdownDoc drops the rest), and the
            // rel guards a link the recipient may open from an email client.
            if (span.Href is not null)
                text = $"<a href=\"{Escape(span.Href)}\" rel=\"noopener noreferrer\">{text}</a>";
            sb.Append(text);
        }
        return sb.ToString();
    }

    private static string Style() => """
        <style>
          :root { color-scheme: light; }
          body { font-family: ui-sans-serif, system-ui, "Segoe UI", Roboto, sans-serif;
                 margin: 0; padding: 2.5rem 1.25rem; color: #12171f; background: #ffffff; line-height: 1.55; }
          main { max-width: 46rem; margin: 0 auto; }
          .doc-title { font-size: 1.65rem; line-height: 1.2; margin: 0; }
          .doc-meta { margin: 0.35rem 0 0; font-size: 0.8rem; color: #5b6472; }
          .doc-meta + * { margin-top: 1.5rem; }
          main > .doc-meta { padding-bottom: 0.9rem; border-bottom: 2px solid #1f6feb; }
          h2 { font-size: 1.2rem; margin: 1.6rem 0 0.5rem; }
          h3 { font-size: 1.05rem; margin: 1.3rem 0 0.4rem; }
          h4, h5, h6 { font-size: 0.95rem; margin: 1.1rem 0 0.35rem; }
          p { margin: 0.6rem 0; }
          ul, ol { margin: 0.6rem 0; padding-left: 1.4rem; }
          li { margin: 0.15rem 0; }
          li.depth-1 { margin-left: 1rem; }
          li.depth-2 { margin-left: 2rem; }
          li.depth-3 { margin-left: 3rem; }
          .table-scroll { overflow-x: auto; margin: 0.9rem 0; }
          table { border-collapse: collapse; width: 100%; font-size: 0.85rem; }
          th, td { border: 1px solid #dce0e5; padding: 0.4rem 0.6rem; text-align: left; vertical-align: top; }
          th { background: #eef0f3; font-weight: 600; }
          tbody tr:nth-child(even) td { background: #f9fafc; }
          pre { background: #f4f6f8; border: 1px solid #dce0e5; border-radius: 4px;
                padding: 0.7rem 0.85rem; overflow-x: auto; font-size: 0.8rem; line-height: 1.45; }
          code { font-family: ui-monospace, SFMono-Regular, Consolas, "DejaVu Sans Mono", monospace;
                 font-size: 0.85em; background: #f4f6f8; padding: 0.05em 0.3em; border-radius: 3px; }
          pre code { background: none; padding: 0; font-size: 1em; }
          blockquote { margin: 0.9rem 0; padding: 0.5rem 0.9rem; background: #f9fafc;
                       border-left: 3px solid #1f6feb; color: #5b6472; font-style: italic; }
          hr { border: 0; border-top: 1px solid #dce0e5; margin: 1.4rem 0; }
          a { color: #1f6feb; }
          /* Printing to PDF from the browser is the fallback path when someone wants a
             PDF of an HTML export; keep the page from breaking mid-table. */
          @media print {
            body { padding: 0; }
            tr, pre, blockquote { break-inside: avoid; }
          }
        </style>
        """;

    private static string Escape(string? value) => ExportService.HtmlEscape(value);
}
