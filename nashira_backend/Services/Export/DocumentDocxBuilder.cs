using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace nashira_backend.Services.Export;

/// <summary>
/// Renders the same block model the PDF and HTML builders take, as a .docx.
/// </summary>
/// <remarks>
/// <para>
/// Third renderer over <see cref="MarkdownDoc"/>, on purpose: a report should be
/// the same report whichever format it is asked for, and the way to keep that
/// true is for every format to read one parsed document rather than re-parse the
/// markdown its own way.
/// </para>
/// <para>
/// No new dependency. <c>DocumentFormat.OpenXml</c> is already referenced for
/// DOCX <em>parsing</em>; this is the same package writing instead of reading.
/// </para>
/// <para>
/// Styles are defined inline rather than pulled from a template. A .docx with no
/// styles part renders with Word's defaults, which are not ours, and shipping a
/// template file would mean a second thing to keep in step with
/// <see cref="DocumentTheme"/>.
/// </para>
/// </remarks>
internal static class DocumentDocxBuilder
{
    public static byte[] Build(string title, IReadOnlyList<MdBlock> blocks, DateTime generatedAtUtc)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body());
            var body = main.Document.Body!;

            main.AddNewPart<StyleDefinitionsPart>().Styles = BuildStyles();

            body.AppendChild(Heading(title, 1));
            body.AppendChild(Muted($"Generated {generatedAtUtc:yyyy-MM-dd HH:mm} UTC"));

            foreach (var block in blocks) Append(body, block);

            // Word treats a section without explicit properties as legacy layout;
            // stating them keeps the page size predictable across viewers.
            body.AppendChild(new SectionProperties(
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1134, Right = 1134, Bottom = 1134, Left = 1134 }));

            main.Document.Save();
        }

        return stream.ToArray();
    }

    private static void Append(Body body, MdBlock block)
    {
        switch (block)
        {
            case MdHeading h:
                // +1 because the document title already occupies level 1.
                body.AppendChild(Heading(MarkdownDoc.PlainText(h.Inline), Math.Min(h.Level + 1, 6)));
                break;

            case MdParagraph p:
                body.AppendChild(Paragraph(p.Inline));
                break;

            case MdList l:
                foreach (var item in l.Items)
                {
                    // Indentation and a literal marker rather than Word's numbering
                    // definitions: numbering needs a NumberingDefinitionsPart whose ids
                    // have to stay unique across the document, and a generated report
                    // has no requirement these lists be continuable or restartable.
                    var marker = l.Ordered ? "1. " : "• ";
                    var para = Paragraph(item.Inline, indentTwips: 360 + item.Depth * 360, prefix: marker);
                    body.AppendChild(para);
                }
                break;

            case MdTable t:
                body.AppendChild(Table(t));
                body.AppendChild(new Paragraph());
                break;

            case MdCode c:
                foreach (var line in c.Text.Replace("\r\n", "\n").Split('\n'))
                    body.AppendChild(Mono(line));
                body.AppendChild(new Paragraph());
                break;

            case MdQuote q:
                body.AppendChild(Paragraph(q.Inline, indentTwips: 360, italic: true));
                break;

            case MdRule:
                body.AppendChild(new Paragraph(new ParagraphProperties(
                    new ParagraphBorders(new BottomBorder
                    {
                        Val = BorderValues.Single,
                        Size = 6,
                        Color = DocumentTheme.RuleHex,
                    }))));
                break;
        }
    }

    private static Paragraph Heading(string text, int level) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = $"Heading{level}" }),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph Muted(string text) =>
        new(new Run(
            new RunProperties(
                new Color { Val = DocumentTheme.MutedHex },
                new FontSize { Val = "18" }),
            new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph Mono(string text) =>
        new(new Run(
            new RunProperties(
                new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" },
                new FontSize { Val = "18" }),
            new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph Paragraph(
        IReadOnlyList<MdSpan> spans, int indentTwips = 0, bool italic = false, string? prefix = null)
    {
        var para = new Paragraph();
        if (indentTwips > 0)
            para.AppendChild(new ParagraphProperties(new Indentation { Left = indentTwips.ToString() }));
        if (prefix is not null)
            para.AppendChild(new Run(new Text(prefix) { Space = SpaceProcessingModeValues.Preserve }));
        foreach (var span in spans) para.AppendChild(Run(span, italic));
        return para;
    }

    private static Run Run(MdSpan span, bool forceItalic = false)
    {
        var props = new RunProperties();
        if (span.Bold) props.AppendChild(new Bold());
        if (span.Italic || forceItalic) props.AppendChild(new Italic());
        if (span.Code)
        {
            props.AppendChild(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
            props.AppendChild(new Color { Val = DocumentTheme.CodeHex });
        }

        // A link's target is written next to its text rather than as a hyperlink
        // relationship: a relationship needs a part-level id, and a report that is
        // printed or pasted loses the target entirely if it only lives in the link.
        var text = span.Href is { Length: > 0 } href && href != span.Text
            ? $"{span.Text} ({href})"
            : span.Text;

        return new Run(props, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Table Table(MdTable t)
    {
        var table = new Table(new TableProperties(
            new TableStyle { Val = "TableGrid" },
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = DocumentTheme.RuleHex },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = DocumentTheme.RuleHex },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = DocumentTheme.RuleHex },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = DocumentTheme.RuleHex },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = DocumentTheme.RuleHex },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = DocumentTheme.RuleHex })));

        if (t.Headers.Count > 0)
        {
            var head = new TableRow();
            foreach (var cell in t.Headers) head.AppendChild(Cell(cell, header: true));
            table.AppendChild(head);
        }

        foreach (var row in t.Rows)
        {
            var tr = new TableRow();
            foreach (var cell in row) tr.AppendChild(Cell(cell, header: false));
            table.AppendChild(tr);
        }

        return table;
    }

    private static TableCell Cell(IReadOnlyList<MdSpan> spans, bool header)
    {
        var para = new Paragraph();
        foreach (var span in spans)
            para.AppendChild(Run(header ? span with { Bold = true } : span));
        if (spans.Count == 0) para.AppendChild(new Run(new Text(string.Empty)));

        var props = new TableCellProperties(new TableCellMargin(
            new LeftMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
            new RightMargin { Width = "80", Type = TableWidthUnitValues.Dxa }));
        if (header)
            props.AppendChild(new Shading { Fill = DocumentTheme.HeaderFillHex, Val = ShadingPatternValues.Clear });

        return new TableCell(props, para);
    }

    private static Styles BuildStyles()
    {
        var styles = new Styles();
        for (var level = 1; level <= 6; level++)
        {
            styles.AppendChild(new Style(
                new StyleName { Val = $"heading {level}" },
                new BasedOn { Val = "Normal" },
                new StyleRunProperties(
                    new Bold(),
                    new Color { Val = DocumentTheme.HeadingHex },
                    new FontSize { Val = (32 - level * 3).ToString() }))
            {
                Type = StyleValues.Paragraph,
                StyleId = $"Heading{level}",
            });
        }
        return styles;
    }
}
