using System.Text.Json.Serialization;
using nashira_backend.Data.Models;
using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Data.DTos.Python;

// Create and update share one body. On update every field is optional and null means
// "leave it"; `python_version: ""` clears it back to the image's python3.
public class WritePythonEnvironment
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("python_version")] public string? PythonVersion { get; set; }
    [JsonPropertyName("requirements")] public string? Requirements { get; set; }
    [JsonPropertyName("is_ssh_fallback")] public bool? IsSshFallback { get; set; }

    public PythonEnvironmentDraft ToDraft() =>
        new(Name, Description, PythonVersion, Requirements, IsSshFallback);
}

public class PythonEnvironmentResponse
{
    [JsonPropertyName("python_environment_id")] public Guid PythonEnvironmentId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("python_version")] public string? PythonVersion { get; set; }
    // Credentials in index URLs are masked (`https://***@host`). Submitting the masked
    // text back unchanged keeps the stored credentials.
    [JsonPropertyName("requirements")] public string Requirements { get; set; } = string.Empty;
    [JsonPropertyName("is_ssh_fallback")] public bool IsSshFallback { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = PythonEnvironment.StatusPending;
    [JsonPropertyName("last_error")] public string? LastError { get; set; }
    [JsonPropertyName("resolved_packages")] public string? ResolvedPackages { get; set; }
    // Over the EFFECTIVE requirements of the last good build: Requirements plus
    // included_packages.
    [JsonPropertyName("requirements_hash")] public string? RequirementsHash { get; set; }
    // The pip specs the Python packages allowlist adds to this environment (rows whose
    // install targets include it), deduplicated and sorted, masked like requirements.
    // Read-only here: they are edited on the allowlist row.
    [JsonPropertyName("included_packages")] public List<string> IncludedPackages { get; set; } = [];
    [JsonPropertyName("built_at")] public DateTime? BuiltAt { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }

    public static PythonEnvironmentResponse From(PythonEnvironment e, IEnumerable<string>? included = null) => new()
    {
        IncludedPackages = (included ?? [])
            .Select(s => PythonEnvironmentRules.MaskCredentials(s) ?? string.Empty)
            .ToList(),
        PythonEnvironmentId = e.PythonEnvironmentId,
        Name = e.Name,
        Description = e.Description,
        PythonVersion = e.PythonVersion,
        Requirements = PythonEnvironmentRules.MaskCredentials(e.Requirements) ?? string.Empty,
        IsSshFallback = e.IsSshFallback,
        Status = e.Status,
        LastError = PythonEnvironmentRules.MaskCredentials(e.LastError),
        ResolvedPackages = PythonEnvironmentRules.MaskCredentials(e.ResolvedPackages),
        RequirementsHash = e.RequirementsHash,
        BuiltAt = e.BuiltAt,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}
