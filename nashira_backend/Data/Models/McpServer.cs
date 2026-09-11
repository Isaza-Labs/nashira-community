using System.Text.Json.Serialization;

namespace nashira_backend.Data.Models;

// A registered external MCP (Model Context Protocol) server. Nashira is the MCP
// *client*: it connects over Streamable HTTP, discovers the server's tools (cached
// as McpTool rows) and lets the agent and workflow nodes call them.
//
// This is the client direction only. Exposing Nashira's own tools *as* an MCP
// server was decided against (netora_refactor.md §6.4) in favour of the REST/SSE
// surface; nothing here reverses that.
public class McpServer : BaseModel
{
    public const string TransportHttp = "http";

    public const string AuthNone = "none";
    public const string AuthApiKey = "api_key";
    public const string AuthBearer = "bearer";
    public const string AuthBasic = "basic";
    public const string AuthHeaders = "headers";
    public const string AuthOAuthClientCredentials = "oauth_client_credentials";
    public const string AuthOAuthAuthorizationCode = "oauth_authorization_code";

    public static readonly string[] AuthTypes =
    [
        AuthNone, AuthApiKey, AuthBearer, AuthBasic, AuthHeaders,
        AuthOAuthClientCredentials, AuthOAuthAuthorizationCode,
    ];

    // MCP-specific status, alongside the Integration.Status* vocabulary the rest of
    // this model reuses: an authorization-code server whose consent has not been
    // granted (or whose refresh token died) needs an admin at a browser, which is a
    // different call to action than "unreachable". Ported from FlowWeaver.
    public const string StatusNeedsAuthorization = "needs_authorization";

    public Guid McpServerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // The server's HTTP endpoint (Streamable HTTP / SSE). Passed through
    // IUrlGuard.EnsureSafe before every connection unless AllowPrivateNetwork.
    public string Url { get; set; } = string.Empty;

    // Only "http" is supported. stdio is deliberately out of scope: it would mean
    // spawning and supervising child processes on the backend host, which is a
    // different security and lifecycle problem than making an outbound request.
    public string Transport { get; set; } = TransportHttp;

    public string AuthType { get; set; } = AuthNone;

    // Auth material as an encrypted McpAuthConfig JSON document.
    //
    // Encrypted rather than the ${secret:...} references Integration uses, because
    // the OAuth grant obtains access and refresh tokens *at runtime*: there is no
    // admin-entered value to point a reference at, and a refresh token has to
    // survive a restart. Never returned by the API — the DTOs expose has_* booleans.
    [JsonIgnore]
    public byte[]? AuthConfigEncrypted { get; set; }

    // Optional: take the secret material from a stored Credential instead of encrypting
    // a copy here. AuthType still says which scheme to use; the credential supplies the
    // values, so one key entered in /admin/credentials serves several servers. Runtime
    // OAuth tokens keep living in AuthConfigEncrypted — they have no credential to come
    // from. Not a hard FK, matching GitRepository.AuthCredentialId.
    public Guid? AuthCredentialId { get; set; }

    // Static, non-secret headers sent on every request (JSON object).
    public string? HeadersJson { get; set; }

    public bool TlsSkipVerify { get; set; }

    // Same semantics as Integration.AllowPrivateNetwork: relaxes RFC-1918, never
    // loopback or the cloud metadata address.
    public bool AllowPrivateNetwork { get; set; }

    public string Status { get; set; } = Integration.StatusUnknown;
    public string? LastCheckError { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? ToolsSyncedAt { get; set; }
    public int ToolCount { get; set; }

    public bool Enabled { get; set; } = true;

    // Whether this server's `readOnlyHint` annotations may skip the confirmation gate.
    //
    // Off by default, and that default is the whole point. Every MCP tool call is
    // confirmed because nothing in the protocol distinguishes a read from a write —
    // except an annotation written by the server itself, which is the party that gains
    // from claiming a destructive tool is harmless. Turning this on says "I run this
    // server and I believe its annotations", which is a statement only an administrator
    // can make.
    public bool TrustToolHints { get; set; }

    public Guid? CreatedBy { get; set; }
}
