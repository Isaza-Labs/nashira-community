namespace nashira_backend.Data.Models;

// An override of one service-level objective's threshold.
//
// Only the number lives here. What each SLO *is* — its label, its unit, whether higher
// or lower is better, and above all how it is computed — is code, because adding an
// objective means writing the query that measures it. A row exists only for an
// objective somebody has moved off its default; the absence of a row is not missing
// configuration, it is "the built-in target still applies".
public class SloTarget : BaseModel
{
    public Guid SloTargetId { get; set; }

    // Matches a key in SloDefinition.All. A row whose key no longer corresponds to a
    // definition is ignored rather than deleted: an objective removed in one release
    // and restored in the next should come back with the threshold it was given.
    public string Key { get; set; } = string.Empty;

    public double Target { get; set; }

    // Who last moved it, for the screen. The audit trail carries the full history; this
    // is so the number can say "changed by rlozada" without a second query.
    public string? UpdatedBy { get; set; }
}
