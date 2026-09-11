using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Mcp;

public class CreateMcpServer
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("auth_type")] public string? AuthType { get; set; }
    // Written, never read back. Shape is McpAuthConfig.
    [JsonPropertyName("auth_config")] public JsonElement? AuthConfig { get; set; }
    [JsonPropertyName("auth_credential_id")] public Guid? AuthCredentialId { get; set; }
    [JsonPropertyName("headers")] public string? Headers { get; set; }
    [JsonPropertyName("tls_skip_verify")] public bool? TlsSkipVerify { get; set; }
    [JsonPropertyName("allow_private_network")] public bool? AllowPrivateNetwork { get; set; }

    // Lets this server's readOnlyHint annotations skip the confirmation gate. Off
    // unless an admin says otherwise — the server is the party that benefits from
    // calling a destructive tool harmless.
    [JsonPropertyName("trust_tool_hints")] public bool? TrustToolHints { get; set; }

    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

public class UpdateMcpServer
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("auth_type")] public string? AuthType { get; set; }
    // Absent leaves the stored credentials alone — the UI cannot echo them back to
    // resubmit, so an omitted field has to mean "unchanged", not "clear".
    [JsonPropertyName("auth_config")] public JsonElement? AuthConfig { get; set; }
    [JsonPropertyName("clear_auth_config")] public bool? ClearAuthConfig { get; set; }

    // Tri-state, like Integration's: absent keeps the link, explicit null detaches.
    // Non-nullable so an absent property stays Undefined instead of collapsing to null.
    [JsonPropertyName("auth_credential_id")] public JsonElement AuthCredentialId { get; set; }

    [JsonPropertyName("headers")] public string? Headers { get; set; }
    [JsonPropertyName("tls_skip_verify")] public bool? TlsSkipVerify { get; set; }
    [JsonPropertyName("allow_private_network")] public bool? AllowPrivateNetwork { get; set; }
    [JsonPropertyName("trust_tool_hints")] public bool? TrustToolHints { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

public class McpServerResponse
{
    [JsonPropertyName("mcp_server_id")] public Guid McpServerId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("transport")] public string Transport { get; set; } = "http";
    [JsonPropertyName("auth_type")] public string AuthType { get; set; } = "none";
    [JsonPropertyName("has_credentials")] public bool HasCredentials { get; set; }
    [JsonPropertyName("auth_credential_id")] public Guid? AuthCredentialId { get; set; }
    [JsonPropertyName("auth_credential_name")] public string? AuthCredentialName { get; set; }
    [JsonPropertyName("tls_skip_verify")] public bool TlsSkipVerify { get; set; }
    [JsonPropertyName("allow_private_network")] public bool AllowPrivateNetwork { get; set; }
    [JsonPropertyName("trust_tool_hints")] public bool TrustToolHints { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown";
    [JsonPropertyName("last_check_error")] public string? LastCheckError { get; set; }
    [JsonPropertyName("last_checked_at")] public DateTime? LastCheckedAt { get; set; }
    [JsonPropertyName("tools_synced_at")] public DateTime? ToolsSyncedAt { get; set; }
    [JsonPropertyName("tool_count")] public int ToolCount { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("is_active")] public bool IsActive { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class McpToolResponse
{
    [JsonPropertyName("mcp_tool_id")] public Guid McpToolId { get; set; }
    [JsonPropertyName("mcp_server_id")] public Guid McpServerId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("input_schema")] public JsonElement InputSchema { get; set; }

    // The server's own claim that this tool changes nothing. Shown so an admin can see
    // what they are trusting before switching trust_tool_hints on.
    [JsonPropertyName("read_only_hint")] public bool ReadOnlyHint { get; set; }

    [JsonPropertyName("disappeared_at")] public DateTime? DisappearedAt { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class UpdateMcpTool
{
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

// The URL the admin's browser must visit to grant consent (oauth/start).
public class McpOAuthStartResponse
{
    [JsonPropertyName("authorization_url")] public string AuthorizationUrl { get; set; } = string.Empty;
}

public class McpSyncResponse
{
    [JsonPropertyName("discovered")] public int Discovered { get; set; }
    [JsonPropertyName("created")] public int Created { get; set; }
    [JsonPropertyName("updated")] public int Updated { get; set; }
    [JsonPropertyName("disappeared")] public int Disappeared { get; set; }
    [JsonPropertyName("reappeared")] public int Reappeared { get; set; }
}

public class McpHealthResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("elapsed_ms")] public int ElapsedMs { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("tool_count")] public int ToolCount { get; set; }
}

public class McpCallRequest
{
    [JsonPropertyName("tool")] public string Tool { get; set; } = string.Empty;
    [JsonPropertyName("arguments")] public JsonElement? Arguments { get; set; }
}

public class McpCallResponse
{
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("structured")] public JsonElement? Structured { get; set; }
    [JsonPropertyName("is_error")] public bool IsError { get; set; }
}
