namespace nashira_backend.Data.Models;

// A tool discovered on an McpServer, upserted from that server's tools/list.
//
// Cached so the agent can see the catalog and a workflow node can reference a tool
// without a live round trip on every render, and so a server going offline degrades
// to "this tool is currently unreachable" instead of "this tool does not exist".
// Unique per (McpServerId, Name) among active rows.
public class McpTool : BaseModel
{
    public Guid McpToolId { get; set; }
    public Guid McpServerId { get; set; }

    // The tool's name on the server — the id passed to tools/call.
    public string Name { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Description { get; set; }

    // JSON Schema of the tool's arguments, exactly as the server reported it. Passed
    // through to the LLM as the tool's parameter schema, so it is stored verbatim
    // rather than re-derived.
    public string InputSchemaJson { get; set; } = "{}";

    // Set when a sync stopped seeing this tool. The row is kept rather than deleted
    // so a workflow still referencing it reports "the server no longer offers this
    // tool" instead of failing to resolve a dangling id.
    public DateTime? DisappearedAt { get; set; }

    // Admin on/off switch, distinct from IsActive (soft delete). A tool disabled by
    // an admin stays disabled across re-syncs.
    public bool Enabled { get; set; } = true;

    // The server's own `readOnlyHint` annotation, as reported by tools/list.
    //
    // A HINT, and recorded as one. The MCP specification is explicit that a client must
    // not make tool-use decisions on annotations from a server it does not trust — the
    // server is the party that benefits from lying. So this is stored for display, and
    // only allowed to skip a confirmation when an admin has vouched for the server
    // (McpServer.TrustToolHints).
    public bool ReadOnlyHint { get; set; }
}
