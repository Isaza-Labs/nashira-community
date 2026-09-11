using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace nashira_backend.Services.Export;

// Renders a parsed markdown document to A4 PDF. The community licence is registered
// once at boot (Program.cs), not here — doing it per call is redundant and surprises
// tests that build a document without going through startup.
internal static class DocumentPdfBuilder
{
    private const float PageMargin = 36f;
    private const float BodySize = 9.5f;

    public static byte[] Build(string title, IReadOnlyList<MdBlock> blocks, DateTime generatedAtUtc) =>
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(PageMargin);
                page.DefaultTextStyle(t => t
                    .FontFamily(DocumentTheme.BodyFonts)
                    .FontSize(BodySize)
                    .FontColor(DocumentTheme.Text)
                    .LineHeight(1.35f)
                    // Lato ligates fi/fl/ti into single glyphs, which look fine and then
                    // ruin the report: searching a reader for "workflow_id" finds nothing
                    // and copying it out pastes a glyph no shell understands. These
                    // documents are full of identifiers, so legibility loses to accuracy.
                    .DisableFontFeature(FontFeatures.StandardLigatures)
                    .DisableFontFeature(FontFeatures.ContextualLigatures));

                page.Content().Column(col =>
                {
                    col.Spacing(7);
                    col.Item().Element(e => ComposeTitle(e, title, generatedAtUtc));
                    foreach (var block in blocks)
                        col.Item().Element(e => ComposeBlock(e, block));
                });

                // The title repeats in the footer rather than in a running header: on a
                // one-page report — which most of these are — a header would just say the
                // same thing twice, an inch apart.
                page.Footer().PaddingTop(8).BorderTop(0.5f).BorderColor(DocumentTheme.Border)
                    .PaddingTop(4).Row(row =>
                    {
                        row.RelativeItem().Text(title)
                            .FontSize(7.5f).FontColor(DocumentTheme.TextMuted);
                        row.ConstantItem(90).AlignRight().Text(t =>
                        {
                            t.DefaultTextStyle(s => s.FontSize(7.5f).FontColor(DocumentTheme.TextMuted));
                            t.Span("Page ");
                            t.CurrentPageNumber();
                            t.Span(" of ");
                            t.TotalPages();
                        });
                    });
            });
        }).GeneratePdf();

    private static void ComposeTitle(IContainer container, string title, DateTime generatedAtUtc)
    {
        container.PaddingBottom(6).Column(col =>
        {
            col.Item().Text(title).FontSize(19).Bold().FontColor(DocumentTheme.Text);
            col.Item().PaddingTop(2).Text($"Generated {generatedAtUtc:yyyy-MM-dd HH:mm} UTC")
                .FontSize(8).FontColor(DocumentTheme.TextMuted);
            col.Item().PaddingTop(6).LineHorizontal(1.5f).LineColor(DocumentTheme.Accent);
        });
    }

    private static void ComposeBlock(IContainer container, MdBlock block)
    {
        switch (block)
        {
            case MdHeading heading:
                ComposeHeading(container, heading);
                break;
            case MdParagraph paragraph:
                container.Text(t => ComposeSpans(t, paragraph.Inline));
                break;
            case MdList list:
                ComposeList(container, list);
                break;
            case MdTable table:
                ComposeTable(container, table);
                break;
            case MdCode code:
                ComposeCode(container, code);
                break;
            case MdQuote quote:
                ComposeQuote(container, quote);
                break;
            case MdRule:
                container.PaddingVertical(4).LineHorizontal(0.75f).LineColor(DocumentTheme.Border);
                break;
        }
    }

    private static void ComposeHeading(IContainer container, MdHeading heading)
    {
        // h1 is the document title in most generated reports, and the title is already
        // rendered above — so the scale starts one step down and never competes with it.
        var (size, top) = heading.Level switch
        {
            1 => (14f, 10f),
            2 => (12f, 8f),
            3 => (10.5f, 6f),
            _ => (BodySize, 5f),
        };

        container.PaddingTop(top).Text(t =>
        {
            t.DefaultTextStyle(s => s.FontSize(size).Bold().FontColor(DocumentTheme.Text));
            ComposeSpans(t, heading.Inline, size);
        });
    }

    private static void ComposeList(IContainer container, MdList list)
    {
        container.Column(col =>
        {
            col.Spacing(2);
            for (var i = 0; i < list.Items.Count; i++)
            {
                var item = list.Items[i];
                var marker = list.Ordered ? $"{i + 1}." : "•";

                col.Item().PaddingLeft(item.Depth * 12).Row(row =>
                {
                    row.ConstantItem(list.Ordered ? 16 : 10)
                        .Text(marker).FontColor(DocumentTheme.TextMuted);
                    row.RelativeItem().Text(t => ComposeSpans(t, item.Inline));
                });
            }
        });
    }

    private static void ComposeTable(IContainer container, MdTable table)
    {
        var columns = Math.Max(1, table.Headers.Count);

        container.Table(t =>
        {
            t.ColumnsDefinition(cd =>
            {
                for (var i = 0; i < columns; i++) cd.RelativeColumn();
            });

            t.Header(h =>
            {
                foreach (var header in table.Headers)
                {
                    h.Cell().Background(DocumentTheme.HeaderFill)
                        .Border(0.5f).BorderColor(DocumentTheme.Border)
                        .Padding(5)
                        .Text(x =>
                        {
                            x.DefaultTextStyle(s => s.FontSize(8).Bold().FontColor(DocumentTheme.Text));
                            ComposeSpans(x, header, 8);
                        });
                }
            });

            for (var r = 0; r < table.Rows.Count; r++)
            {
                var fill = r % 2 == 1 ? DocumentTheme.SurfaceAlt : DocumentTheme.Surface;
                foreach (var cell in table.Rows[r])
                {
                    t.Cell().Background(fill)
                        .Border(0.5f).BorderColor(DocumentTheme.Border)
                        .Padding(5)
                        .Text(x =>
                        {
                            x.DefaultTextStyle(s => s.FontSize(8));
                            ComposeSpans(x, cell, 8);
                        });
                }
            }
        });
    }

    private static void ComposeCode(IContainer container, MdCode code)
    {
        container.Background(DocumentTheme.CodeFill)
            .Border(0.5f).BorderColor(DocumentTheme.Border)
            .Padding(7)
            .Text(code.Text)
            .FontFamily(DocumentTheme.MonoFonts)
            .FontSize(8)
            .LineHeight(1.25f)
            .FontColor(DocumentTheme.Text);
    }

    private static void ComposeQuote(IContainer container, MdQuote quote)
    {
        container.Row(row =>
        {
            row.ConstantItem(2.5f).Background(DocumentTheme.Accent);
            row.RelativeItem().Background(DocumentTheme.SurfaceAlt).Padding(7)
                .Text(t =>
                {
                    t.DefaultTextStyle(s => s.Italic().FontColor(DocumentTheme.TextMuted));
                    ComposeSpans(t, quote.Inline);
                });
        });
    }

    private static void ComposeSpans(TextDescriptor text, IReadOnlyList<MdSpan> spans, float size = BodySize)
    {
        if (spans.Count == 0)
        {
            // An empty cell still has to occupy its row; QuestPDF renders nothing at all
            // for a text element with no spans, which collapses the border.
            text.Span(string.Empty);
            return;
        }

        foreach (var span in spans)
        {
            if (span.Href is not null)
            {
                text.Hyperlink(span.Text, span.Href).FontColor(DocumentTheme.Accent).Underline();
                continue;
            }

            var descriptor = span.Code
                ? text.Span(span.Text).FontFamily(DocumentTheme.MonoFonts)
                    .FontSize(size - 0.5f).BackgroundColor(DocumentTheme.CodeFill)
                : text.Span(span.Text);

            if (span.Bold) descriptor = descriptor.Bold();
            if (span.Italic) descriptor.Italic();
        }
    }
}
