using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Integration;

public class CreateIntegration
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("base_url")] public string BaseUrl { get; set; } = string.Empty;

    // Accepts the object form too — see JsonStringOrObjectConverter.
    [JsonPropertyName("auth_config")]
    [JsonConverter(typeof(JsonStringOrObjectConverter))]
    public string? AuthConfig { get; set; }

    // Not a stored field: auth_method is DERIVED from auth_config (or from the linked
    // credential when the config declares no method). Bound only so that sending it is
    // answered with a 400 that names the right knob, instead of being dropped silently
    // and then contradicted by the response.
    [JsonPropertyName("auth_method")] public string? AuthMethod { get; set; }

    [JsonPropertyName("auth_credential_id")] public Guid? AuthCredentialId { get; set; }
    [JsonPropertyName("headers")] public string? Headers { get; set; }
    [JsonPropertyName("verify_ssl")] public bool? VerifySsl { get; set; }
    [JsonPropertyName("allow_private_network")] public bool? AllowPrivateNetwork { get; set; }
    [JsonPropertyName("health_check_path")] public string? HealthCheckPath { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

public class UpdateIntegration
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }

    [JsonPropertyName("auth_config")]
    [JsonConverter(typeof(JsonStringOrObjectConverter))]
    public string? AuthConfig { get; set; }

    // Derived, never stored — see CreateIntegration.AuthMethod.
    [JsonPropertyName("auth_method")] public string? AuthMethod { get; set; }

    // Tri-state on purpose. Absent from the body means "leave the link alone"; an
    // explicit null detaches the credential and falls back to auth_config.
    //
    // Non-nullable JsonElement is what makes that distinction possible: an absent
    // property is never assigned and stays Undefined, while `Guid?` — and even
    // `JsonElement?` — collapse both cases to C# null.
    [JsonPropertyName("auth_credential_id")] public System.Text.Json.JsonElement AuthCredentialId { get; set; }

    [JsonPropertyName("headers")] public string? Headers { get; set; }
    [JsonPropertyName("verify_ssl")] public bool? VerifySsl { get; set; }
    [JsonPropertyName("allow_private_network")] public bool? AllowPrivateNetwork { get; set; }
    [JsonPropertyName("health_check_path")] public string? HealthCheckPath { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

// The non-secret half of an integration's auth_config: which scheme, which header, which
// prefix. Not the token, the password or the client secret — those never leave.
//
// Returned because an edit form that cannot see them cannot preserve them: changing a
// description meant re-deriving the NetBox "Token" prefix from memory, and a field the
// user cannot see is a field they will overwrite by accident.
public class IntegrationAuthShape
{
    [JsonPropertyName("method")] public string Method { get; set; } = "none";

    // `token` method: the Authorization scheme (NetBox wants "Token").
    [JsonPropertyName("prefix")] public string? Prefix { get; set; }

    // `api_key` method: which header carries the key.
    [JsonPropertyName("header")] public string? Header { get; set; }

    // `basic` / oauth2: the non-secret half of the pair.
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("token_url")] public string? TokenUrl { get; set; }
    [JsonPropertyName("client_id")] public string? ClientId { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
}

// auth_config is never returned as stored: it carries ${secret:...} references that name
// the secret store's layout, and echoing them back is free reconnaissance. The client
// gets the method, whether credentials are present, and the non-secret shape.
//
// auth_credential_id IS returned — it is an id, not secret material, and the form
// needs it to show which stored credential is in use. Same call as
// GitRepositoryResponse.auth_credential_id.
public class IntegrationResponse
{
    [JsonPropertyName("integration_id")] public Guid IntegrationId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("base_url")] public string BaseUrl { get; set; } = string.Empty;
    [JsonPropertyName("auth_method")] public string AuthMethod { get; set; } = "none";
    [JsonPropertyName("has_credentials")] public bool HasCredentials { get; set; }

    // auth_config carries its own secret material, which takes precedence over the
    // linked credential. Without this, an integration that ignores its credential
    // looks identical to one that uses it.
    [JsonPropertyName("has_inline_credentials")] public bool HasInlineCredentials { get; set; }

    [JsonPropertyName("auth_shape")] public IntegrationAuthShape? AuthShape { get; set; }

    // The static headers, exactly as stored. Declared non-secret by the field's own
    // contract (Authorization is refused here — it belongs to auth_config), and an edit
    // form that cannot read them cannot show what is configured.
    [JsonPropertyName("headers")] public string? Headers { get; set; }
    [JsonPropertyName("auth_credential_id")] public Guid? AuthCredentialId { get; set; }
    [JsonPropertyName("auth_credential_name")] public string? AuthCredentialName { get; set; }
    [JsonPropertyName("verify_ssl")] public bool VerifySsl { get; set; }
    [JsonPropertyName("allow_private_network")] public bool AllowPrivateNetwork { get; set; }
    [JsonPropertyName("health_check_path")] public string? HealthCheckPath { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown";
    [JsonPropertyName("last_check_error")] public string? LastCheckError { get; set; }
    [JsonPropertyName("last_checked_at")] public DateTime? LastCheckedAt { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("spec_count")] public int SpecCount { get; set; }
    [JsonPropertyName("action_count")] public int ActionCount { get; set; }
    [JsonPropertyName("is_active")] public bool IsActive { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class IntegrationHealthResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("status_code")] public int? StatusCode { get; set; }
    [JsonPropertyName("elapsed_ms")] public int ElapsedMs { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public class ActionSyncResponse
{
    [JsonPropertyName("created")] public int Created { get; set; }
    [JsonPropertyName("updated")] public int Updated { get; set; }
    [JsonPropertyName("unchanged")] public int Unchanged { get; set; }
    [JsonPropertyName("disappeared")] public int Disappeared { get; set; }
    [JsonPropertyName("spec_count")] public int SpecCount { get; set; }
}

public class UpdateIntegrationAction
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("read_only")] public bool? ReadOnly { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

public class IntegrationActionResponse
{
    [JsonPropertyName("integration_action_id")] public Guid IntegrationActionId { get; set; }
    [JsonPropertyName("integration_id")] public Guid IntegrationId { get; set; }
    [JsonPropertyName("operation_id")] public string? OperationId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("read_only")] public bool ReadOnly { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("is_active")] public bool IsActive { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class IntegrationActionDetailResponse : IntegrationActionResponse
{
    [JsonPropertyName("path_params")] public string? PathParams { get; set; }
    [JsonPropertyName("query_params")] public string? QueryParams { get; set; }
    [JsonPropertyName("request_body")] public string? RequestBody { get; set; }
}
