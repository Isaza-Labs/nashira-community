using System.Text;
using System.Text.RegularExpressions;

namespace nashira_backend.Services.Export;

// A written report arrives from the agent as markdown — the format a model writes
// fluently, and the one a human can still read if every renderer fails. This turns it
// into a block list that the PDF and HTML builders both walk, so a report looks like
// the same document whichever format was asked for.
//
// The supported subset is what a report actually uses: ATX headings, paragraphs,
// bullet/numbered lists, GFM pipe tables, fenced code, blockquotes, rules, and inline
// bold/italic/code/links. Anything else degrades to text rather than failing — a
// half-understood report still beats no file.
internal abstract record MdBlock;

internal sealed record MdHeading(int Level, IReadOnlyList<MdSpan> Inline) : MdBlock;

internal sealed record MdParagraph(IReadOnlyList<MdSpan> Inline) : MdBlock;

internal sealed record MdList(bool Ordered, IReadOnlyList<MdListItem> Items) : MdBlock;

internal sealed record MdTable(
    IReadOnlyList<IReadOnlyList<MdSpan>> Headers,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MdSpan>>> Rows) : MdBlock;

internal sealed record MdCode(string? Language, string Text) : MdBlock;

internal sealed record MdQuote(IReadOnlyList<MdSpan> Inline) : MdBlock;

internal sealed record MdRule : MdBlock;

internal sealed record MdListItem(int Depth, IReadOnlyList<MdSpan> Inline);

// One run of text with the marks that apply to it. Flat by design: nested emphasis is
// rare in generated reports and a tree would buy nothing the renderers can express.
internal sealed record MdSpan(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    string? Href = null);

internal static class MarkdownDoc
{
    private static readonly Regex HeadingRe = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex RuleRe = new(@"^\s{0,3}(?:-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Compiled);
    private static readonly Regex BulletRe = new(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex OrderedRe = new(@"^(\s*)\d{1,9}[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex FenceRe = new(@"^\s{0,3}(`{3,}|~{3,})\s*([\w+#-]*)\s*$", RegexOptions.Compiled);
    private static readonly Regex QuoteRe = new(@"^\s{0,3}>\s?(.*)$", RegexOptions.Compiled);

    // A GFM delimiter row: | --- | :---: |. This is what separates a table from a
    // paragraph that happens to contain pipes, so it is matched strictly.
    private static readonly Regex TableDelimiterRe =
        new(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$", RegexOptions.Compiled);

    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            if (lines[i].Trim().Length == 0) { i++; continue; }

            // Fenced code first: inside a fence nothing else is markup, which is the
            // whole point of pasting CLI output into a report.
            var fence = FenceRe.Match(lines[i]);
            if (fence.Success)
            {
                blocks.Add(ReadFence(lines, ref i, fence));
                continue;
            }

            if (RuleRe.IsMatch(lines[i])) { blocks.Add(new MdRule()); i++; continue; }

            var heading = HeadingRe.Match(lines[i]);
            if (heading.Success)
            {
                blocks.Add(new MdHeading(heading.Groups[1].Value.Length, ParseInline(heading.Groups[2].Value)));
                i++;
                continue;
            }

            if (QuoteRe.IsMatch(lines[i]))
            {
                var quoted = new List<string>();
                for (Match m; i < lines.Length && (m = QuoteRe.Match(lines[i])).Success; i++)
                    quoted.Add(m.Groups[1].Value);
                blocks.Add(new MdQuote(ParseInline(string.Join(' ', quoted).Trim())));
                continue;
            }

            if (IsTableStart(lines, i)) { blocks.Add(ReadTable(lines, ref i)); continue; }

            if (BulletRe.IsMatch(lines[i]) || OrderedRe.IsMatch(lines[i]))
            {
                blocks.Add(ReadList(lines, ref i));
                continue;
            }

            // Paragraph: everything up to a blank line or the start of another block.
            var paragraph = new List<string>();
            while (i < lines.Length && lines[i].Trim().Length > 0 && !StartsBlock(lines, i))
            {
                paragraph.Add(lines[i].Trim());
                i++;
            }
            if (paragraph.Count > 0) blocks.Add(new MdParagraph(ParseInline(string.Join(' ', paragraph))));
        }

        return blocks;
    }

    private static MdCode ReadFence(string[] lines, ref int i, Match fence)
    {
        var marker = fence.Groups[1].Value;
        var language = fence.Groups[2].Value;
        var body = new StringBuilder();

        i++;
        while (i < lines.Length && !IsClosingFence(lines[i], marker))
        {
            body.Append(lines[i]).Append('\n');
            i++;
        }
        if (i < lines.Length) i++; // consume the closing fence

        return new MdCode(language.Length == 0 ? null : language, body.ToString().TrimEnd('\n'));
    }

    private static bool IsClosingFence(string line, string marker)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith(marker, StringComparison.Ordinal)
               && trimmed.TrimEnd(marker[0]).Length == 0;
    }

    private static bool IsTableStart(string[] lines, int i) =>
        lines[i].Contains('|')
        && i + 1 < lines.Length
        && lines[i + 1].Contains('-')
        && TableDelimiterRe.IsMatch(lines[i + 1]);

    private static MdTable ReadTable(string[] lines, ref int i)
    {
        var headers = SplitRow(lines[i]);
        i += 2; // header + delimiter

        var rows = new List<IReadOnlyList<IReadOnlyList<MdSpan>>>();
        while (i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0)
        {
            var cells = SplitRow(lines[i]);
            // Ragged rows are normalized to the header width: a short row would
            // otherwise shift every following cell into the wrong column.
            while (cells.Count < headers.Count) cells.Add(ParseInline(string.Empty));
            if (cells.Count > headers.Count) cells = cells.Take(headers.Count).ToList();
            rows.Add(cells);
            i++;
        }

        return new MdTable(headers, rows);
    }

    private static MdList ReadList(string[] lines, ref int i)
    {
        var ordered = OrderedRe.IsMatch(lines[i]);
        var items = new List<MdListItem>();

        while (i < lines.Length)
        {
            var item = (ordered ? OrderedRe : BulletRe).Match(lines[i]);
            if (item.Success)
            {
                var indent = item.Groups[1].Value.Replace("\t", "    ").Length;
                items.Add(new MdListItem(Math.Min(indent / 2, 3), ParseInline(item.Groups[2].Value)));
                i++;
                continue;
            }

            // An indented non-item line continues the item above it. Anything else —
            // including a list that switches marker style — ends the block, so the
            // renderer never has to guess whether to number the items.
            var continues = items.Count > 0
                            && lines[i].StartsWith("  ", StringComparison.Ordinal)
                            && lines[i].Trim().Length > 0
                            && !BulletRe.IsMatch(lines[i])
                            && !OrderedRe.IsMatch(lines[i]);
            if (!continues) break;

            var last = items[^1];
            items[^1] = last with { Inline = Concat(last.Inline, ParseInline(" " + lines[i].Trim())) };
            i++;
        }

        return new MdList(ordered, items);
    }

    private static bool StartsBlock(string[] lines, int i) =>
        HeadingRe.IsMatch(lines[i])
        || RuleRe.IsMatch(lines[i])
        || FenceRe.IsMatch(lines[i])
        || QuoteRe.IsMatch(lines[i])
        || BulletRe.IsMatch(lines[i])
        || OrderedRe.IsMatch(lines[i])
        || IsTableStart(lines, i);

    private static IReadOnlyList<MdSpan> Concat(IReadOnlyList<MdSpan> a, IReadOnlyList<MdSpan> b)
    {
        var list = a.ToList();
        list.AddRange(b);
        return list;
    }

    private static List<IReadOnlyList<MdSpan>> SplitRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal))
            trimmed = trimmed[..^1];

        var cells = new List<IReadOnlyList<MdSpan>>();
        var current = new StringBuilder();

        for (var i = 0; i < trimmed.Length; i++)
        {
            // An escaped pipe is how the markdown exporter protects one inside a cell;
            // splitting on it would add a phantom column on the way back in.
            if (trimmed[i] == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                current.Append('|');
                i++;
                continue;
            }
            if (trimmed[i] == '|')
            {
                cells.Add(ParseInline(CellText(current.ToString())));
                current.Clear();
                continue;
            }
            current.Append(trimmed[i]);
        }
        cells.Add(ParseInline(CellText(current.ToString())));

        return cells;
    }

    // The markdown exporter writes newlines inside a cell as <br>; bring them back so a
    // round-tripped table does not read as one run-on line.
    private static string CellText(string raw) =>
        Regex.Replace(raw.Trim(), "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);

    // Inline scanner. Code spans win over everything else (that is what backticks mean),
    // then links, then emphasis. An unmatched marker stays literal instead of eating the
    // rest of the line — a lone asterisk in CLI output is common and must survive.
    public static IReadOnlyList<MdSpan> ParseInline(string? text)
    {
        var spans = new List<MdSpan>();
        if (string.IsNullOrEmpty(text)) return spans;

        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0) return;
            spans.Add(new MdSpan(buffer.ToString()));
            buffer.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length && "*_`[]|\\".Contains(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i++;
                continue;
            }

            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    Flush();
                    spans.Add(new MdSpan(text[(i + 1)..close], Code: true));
                    i = close;
                    continue;
                }
            }

            if (c == '[' && TryReadLink(text, i, out var link, out var consumed))
            {
                Flush();
                spans.Add(link!);
                i = consumed;
                continue;
            }

            if ((c == '*' || c == '_') && TryReadEmphasis(text, i, out var emphasized, out var end))
            {
                Flush();
                spans.AddRange(emphasized!);
                i = end;
                continue;
            }

            buffer.Append(c);
        }

        Flush();
        return spans;
    }

    private static bool TryReadLink(string text, int start, out MdSpan? span, out int end)
    {
        span = null;
        end = start;

        var closeText = text.IndexOf(']', start + 1);
        if (closeText < 0 || closeText + 1 >= text.Length || text[closeText + 1] != '(') return false;

        var closeHref = text.IndexOf(')', closeText + 2);
        if (closeHref < 0) return false;

        var label = text[(start + 1)..closeText];
        var href = text[(closeText + 2)..closeHref].Trim();
        span = new MdSpan(label.Length == 0 ? href : label, Href: SafeHref(href));
        end = closeHref;
        return true;
    }

    private static bool TryReadEmphasis(string text, int start, out IReadOnlyList<MdSpan>? spans, out int end)
    {
        spans = null;
        end = start;

        var c = text[start];
        var strong = start + 1 < text.Length && text[start + 1] == c;
        var marker = strong ? new string(c, 2) : c.ToString();

        // `snake_case_words` must not be read as emphasis.
        if (c == '_' && start > 0 && char.IsLetterOrDigit(text[start - 1])) return false;

        var close = text.IndexOf(marker, start + marker.Length, StringComparison.Ordinal);
        if (close <= start + marker.Length) return false;

        var inner = text[(start + marker.Length)..close];
        if (inner.Trim().Length == 0) return false;

        spans = ParseInline(inner)
            .Select(s => strong ? s with { Bold = true } : s with { Italic = true })
            .ToList();
        end = close + marker.Length - 1;
        return true;
    }

    // Only http(s) survives as a live link. A `javascript:` or `data:` href in a
    // generated document is never legitimate, and both HTML and PDF readers will
    // happily follow one.
    private static string? SafeHref(string href) =>
        href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? href
            : null;

    // Flattens spans back to plain text (PDF table cells, tests).
    public static string PlainText(IEnumerable<MdSpan> spans) =>
        string.Concat(spans.Select(s => s.Text));
}
