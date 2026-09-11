namespace nashira_backend.Data.Models;

// One row = one OpenAPI 3.x YAML spec for a tenant. `Api` is the identifier the
// agent passes to discover/detail/execute (e.g. "netbox"). Base URL + auth make
// the spec self-contained so `execute_operation` (Phase 3 Slice B) can call it.
public class AiApiSpec : BaseModel
{
    public Guid AiApiSpecId { get; set; }

    // Identifier used by the agent (e.g. "netbox"). Unique within a company.
    public string Api { get; set; } = string.Empty;

    // Raw OpenAPI YAML, parsed by YamlSpecIndex into ApiOperation[].
    public string Content { get; set; } = string.Empty;

    // Cached count of HTTP operations parsed from Content (recomputed on upsert).
    public int OperationCount { get; set; }

    // Execution config (used by execute_operation).
    public string? BaseUrl { get; set; }
    public string AuthType { get; set; } = "none"; // none | token | bearer | basic | header
    public string? AuthConfig { get; set; }         // JSON; values may hold ${secret:...} refs
    public bool VerifySsl { get; set; } = true;

    // SSRF guard opt-out for THIS spec's calls. Defaults TRUE — not as a shrug,
    // but because the executor historically hardcoded allowPrivate: true, so true
    // is the exact behaviour every existing spec was configured under; a
    // restrictive default would have silently broken on-prem NetBox specs on
    // upgrade. What changes is that an admin can now turn it OFF per spec.
    // Loopback and the cloud metadata address stay blocked regardless (UrlGuard).
    public bool AllowPrivateNetwork { get; set; } = true;

    public Guid? CreatedBy { get; set; }

    // Optional link to an integration. When set, the operations in this spec are
    // understood to target that integration and to resolve credentials from it;
    // the spec's own BaseUrl/AuthType/AuthConfig stay authoritative when they are
    // filled in, so a linked spec degrades to exactly today's behaviour. Null =
    // global spec, reusable with no automatic credential pairing.
    //
    // Unconstrained Guid?, not an FK — see AiPromptSkill.IntegrationId.
    public Guid? IntegrationId { get; set; }

    // For the built-in Specs/*.yaml rows only: the hash of the file content this row
    // was last seeded or refreshed from.
    //
    // These specs document THIS backend's own API, so the file is the source of truth
    // and the row is a cache of it — but the seeder only ever inserted, never updated,
    // so a row seeded before an endpoint existed kept describing the older API forever.
    // That is not hypothetical: na_admin_readonly went a release without the audit
    // query parameters the endpoint had gained, which is exactly the same as the agent
    // not having them.
    //
    // Refreshing unconditionally would revert an admin who edited a built-in spec in
    // place (trimming operations to save context is a real thing people do). Comparing
    // this hash against the row's current content separates the two cases: equal means
    // nobody has touched it since we wrote it, so the file may replace it; different
    // means someone has, so it is left alone. Null is a row from before this column —
    // adopted once, then tracked.
    //
    // Third-party specs never carry one: they have no shipped file behind them and are
    // never refreshed.
    public string? ShippedContentHash { get; set; }
}
