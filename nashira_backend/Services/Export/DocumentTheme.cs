namespace nashira_backend.Services.Export;

// One palette for both document renderers. The HTML exporter has always used these
// greys; the PDF builder reads from the same place so a report handed over as a PDF
// and the same report as HTML are recognisably the same document.
internal static class DocumentTheme
{
    public const string Text = "#12171f";
    public const string TextMuted = "#5b6472";
    public const string Border = "#dce0e5";
    public const string Surface = "#ffffff";
    public const string SurfaceAlt = "#f9fafc";
    public const string HeaderFill = "#eef0f3";
    public const string Accent = "#1f6feb";
    public const string CodeFill = "#f4f6f8";

    // Body text is Lato: QuestPDF bundles it, so a container with no fonts installed
    // renders exactly like a developer machine. The mono chain is best-effort for the
    // same reason — CLI output wants alignment, but nothing is guaranteed on a slim
    // base image, so Lato closes the list rather than letting resolution fail.
    // OpenXML writes colours as RRGGBB with no leading '#'. Derived from the CSS
    // values above rather than repeated, so the DOCX cannot drift from the PDF and
    // the HTML the day a colour changes.
    public static string Hex(string css) => css.TrimStart('#').ToUpperInvariant();

    public static string HeadingHex => Hex(Text);
    public static string MutedHex => Hex(TextMuted);
    public static string RuleHex => Hex(Border);
    public static string CodeHex => Hex(Accent);
    public static string HeaderFillHex => Hex(HeaderFill);

    public static readonly string[] BodyFonts = ["Lato"];
    public static readonly string[] MonoFonts = ["Consolas", "DejaVu Sans Mono", "Courier New", "Lato"];
}
