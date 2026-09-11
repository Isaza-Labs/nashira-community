namespace nashira_backend.Data.Models;

// A generated report kept for later reading.
//
// Distinct from ExportArtifact on purpose, and the difference is retention rather
// than format: an export is a throwaway CSV someone downloads once, while a report
// is evidence a run produced and that somebody may come back to. Merging them
// would mean giving the existing export table an expiry it was never designed to
// have, and migrating rows that nobody wants kept.
public class ReportArtifact : BaseModel
{
    public Guid ReportArtifactId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    public string ContentType { get; set; } = "text/markdown";
    public string FileName { get; set; } = string.Empty;

    // Bytes in the row, as ExportArtifact already does. Fine at report sizes; if
    // these grow, both tables move to blob storage together rather than diverging.
    public byte[] Content { get; set; } = [];
    public int SizeBytes { get; set; }

    // What produced it, when a workflow did.
    public Guid? WorkflowRunId { get; set; }
    public Guid? WorkflowId { get; set; }

    // Null means keep indefinitely. Indexed so a retention sweeper can find expired
    // rows cheaply — note there is no sweeper yet, so today this is a filter the
    // list endpoint honours rather than a deletion that happens.
    public DateTime? ExpiresAt { get; set; }

    public Guid? CreatedBy { get; set; }
}

// A UI colour theme. Per-user unless shared, in which case it is offered to
// everyone — so flipping `IsShared` is an admin act, not a personal preference.
public class Theme : BaseModel
{
    public Guid ThemeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // JSON object of token → colour. Kept opaque here: the frontend owns which
    // tokens exist, and validating them in the backend would mean redeploying it
    // every time the design system grows a variable.
    public string ColorsJson { get; set; } = "{}";

    // JSON object of style setting -> value: corner roundness, interface scale,
    // font stacks, heading weight. This one IS validated on the way in
    // (Services/Themes/ThemeSettingsValidator), unlike ColorsJson above, and the
    // asymmetry is deliberate: a colour can only ever come back out as a colour,
    // while these values are interpolated into font-family and border-radius
    // declarations on a page every user loads once the theme is shared. The
    // vocabulary is also small and stable, so it costs nothing to pin down.
    //
    // "{}" means the theme carries no style overrides and inherits app.css --
    // which is exactly what every theme saved before this column existed does.
    public string SettingsJson { get; set; } = "{}";

    public bool IsShared { get; set; }
    public Guid? OwnerUserId { get; set; }
}
