using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Modules;

// Effective state of one module. `id` is the public kebab-case key from the catalog,
// never the enum name, and `dependencies` lists the direct requirements only.
public sealed record ModuleManifestEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("configurable")] bool Configurable,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<string> Dependencies);

// GET /api/modules. Disabled modules are included: the SPA decides what to offer from
// this document alone, and an operator diagnosing a missing page needs to see that the
// module exists and is off rather than infer it from an absence.
public sealed record ModuleManifestResponse(
    [property: JsonPropertyName("configuration_mode")] string ConfigurationMode,
    [property: JsonPropertyName("modules")] IReadOnlyList<ModuleManifestEntry> Modules);

// The 503 body for an endpoint whose module is off. Same RFC 7807 shape the
// DomainExceptionHandler produces — including the flat `error` mirror for simple
// clients — plus `module`, so the SPA can react without parsing the message.
public sealed record ModuleDisabledProblem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("module")] string Module);
