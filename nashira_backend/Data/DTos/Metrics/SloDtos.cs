using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Metrics;

// One objective as the screen sees it. Both the effective target and the built-in one
// are sent: without the default, the UI cannot offer "put it back" without hard-coding
// a copy of the number, and a second copy is a second thing to get wrong.
public class SloDto
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("unit")] public string Unit { get; set; } = string.Empty;
    [JsonPropertyName("target")] public double Target { get; set; }
    [JsonPropertyName("default_target")] public double DefaultTarget { get; set; }

    // Null means the window held nothing to measure — which is not zero, and is drawn
    // as "no signal" rather than as a breach.
    [JsonPropertyName("value")] public double? Value { get; set; }

    /// <summary>"lower" or "higher" — which direction meets the objective.</summary>
    [JsonPropertyName("better")] public string Better { get; set; } = string.Empty;

    [JsonPropertyName("method")] public string Method { get; set; } = string.Empty;
    [JsonPropertyName("breach")] public bool Breach { get; set; }

    // Null when the built-in target still applies.
    [JsonPropertyName("updated_by")] public string? UpdatedBy { get; set; }
}

public class SloSnapshotResponse
{
    [JsonPropertyName("days")] public int Days { get; set; }
    [JsonPropertyName("from")] public DateTime From { get; set; }
    [JsonPropertyName("slos")] public List<SloDto> Slos { get; set; } = [];
}

public class SloTargetRequest
{
    // Bounded rather than merely finite. A target of 0 for a "lower is better"
    // objective can never be met, and one of 10^12 can never be missed; both turn the
    // objective into decoration that always reads the same colour.
    [Required]
    [Range(0.000001, 1_000_000_000)]
    [JsonPropertyName("target")]
    public double Target { get; set; }
}
