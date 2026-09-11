namespace nashira_backend.Data.Models;

// An external system Nashira talks to: a base URL, how to authenticate against it,
// and a catalog of IntegrationAction rows describing its operations.
//
// This is the generalization of the execution config that used to live only on
// AiApiSpec (BaseUrl / AuthType / AuthConfig / VerifySsl). A spec or a prompt skill
// linked through IntegrationId inherits this integration's URL and credentials, so
// the same NetBox is configured once and reused by the spec catalog, the workflow
// engine and the agent instead of three times.
//
// AuthConfig deliberately holds `${secret:secret:<name>:value}` references rather than
// ciphertext: it is the pattern AiApiSpec and InventorySource already use, secrets
// stay in one store with one rotation path, and a config row is safe to export.
// McpServer is the exception and encrypts, because OAuth refresh tokens are obtained
// at runtime and cannot be expressed as a reference to something an admin typed.
public class Integration : BaseModel
{
    public const string StatusUnknown = "unknown";
    public const string StatusHealthy = "healthy";
    public const string StatusDegraded = "degraded";
    public const string StatusUnreachable = "unreachable";

    public Guid IntegrationId { get; set; }
    public string Name { get; set; } = string.Empty;

    // Stable cross-instance identity, derived from Name at creation and never
    // changed afterwards. An exported workflow or spec bundle names the integration
    // by slug, so renaming it must not break bundles already shared.
    public string Slug { get; set; } = string.Empty;

    // Free-form kind ("netbox", "servicenow", "infoblox", "awx", ...). Not an enum:
    // adding a system must not require a migration.
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string BaseUrl { get; set; } = string.Empty;

    // JSON: { "method": "...", "token": "...", "username": ..., "password": ...,
    //         "header": ..., "prefix": ..., plus the oauth2_client_credentials
    //         fields }. Values may hold ${secret:...} references, which are
    //         resolved immediately before the request leaves the process.
    public string? AuthConfig { get; set; }

    // Optional: take the secret material from a stored Credential instead of inlining
    // it here. AuthConfig then carries only the SHAPE of the auth (which method, which
    // header, which scheme prefix) and the credential supplies the values, so the same
    // NetBox token is entered once and reused by every integration pointing at it.
    // Deliberately not a hard FK, matching GitRepository.AuthCredentialId: deleting a
    // credential must not be blocked by whoever happens to reference it.
    public Guid? AuthCredentialId { get; set; }

    // Static, non-secret headers sent on every request (JSON object).
    public string? HeadersJson { get; set; }

    public bool VerifySsl { get; set; } = true;

    // SSRF guard opt-out for THIS integration only. The default blocks private,
    // loopback and link-local ranges. Self-hosted deployments where NetBox and
    // friends live on RFC-1918 networks flip this per integration. It never
    // unblocks loopback or 169.254.169.254 — see UrlGuard.
    public bool AllowPrivateNetwork { get; set; }

    // Optional path appended to BaseUrl for the health probe (e.g. "/api/status").
    // Empty probes the base URL itself.
    public string? HealthCheckPath { get; set; }

    public string Status { get; set; } = StatusUnknown;
    public string? LastCheckError { get; set; }
    public DateTime? LastCheckedAt { get; set; }

    // Admin on/off switch, distinct from IsActive (the soft-delete flag). A disabled
    // integration keeps its config and catalog but refuses to execute.
    public bool Enabled { get; set; } = true;

    public Guid? CreatedBy { get; set; }
}
