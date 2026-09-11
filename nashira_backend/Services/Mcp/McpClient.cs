using System.Text;
using System.Text.Json;
using nashira_backend.Data.Models;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SdkClient = ModelContextProtocol.Client.McpClient;

namespace nashira_backend.Services.Mcp;

public interface IMcpClient
{
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(McpServer server, CancellationToken ct);
    Task<McpCallResult> CallToolAsync(McpServer server, string toolName, JsonElement arguments, CancellationToken ct);
}

// Talks to a remote MCP server over Streamable HTTP using the official SDK.
//
// A session is opened per operation rather than pooled. MCP sessions are stateful
// and a pooled one would have to be invalidated on every credential rotation,
// server restart and network blip; at the call rate an agent produces, the
// handshake is not the bottleneck.
public sealed class McpClient : IMcpClient
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    // The agent path caps tool output centrally for the LLM budget; this is the
    // floor that keeps a runaway server from materialising an unbounded string.
    private const int MaxContentChars = 100_000;

    private static readonly McpClientOptions ClientOptions = new()
    {
        ClientInfo = new Implementation { Name = "nashira", Version = "1.0" },
    };

    private readonly IMcpConnectionFactory _factory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;

    public McpClient(
        IMcpConnectionFactory factory, IHttpClientFactory httpFactory, ILoggerFactory loggerFactory)
    {
        _factory = factory;
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
    }

    public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(McpServer server, CancellationToken ct)
        => WithClientAsync(server, async (client, opCt) =>
        {
            var tools = await client.ListToolsAsync((RequestOptions?)null, opCt);
            // Clone: the schema has to outlive the SDK's session document.
            IReadOnlyList<McpToolDescriptor> result = tools
                .Select(t => new McpToolDescriptor(
                    t.Name, t.Title, t.Description, t.JsonSchema.Clone(),
                    // An absent annotation is not a claim of harmlessness.
                    t.ProtocolTool.Annotations?.ReadOnlyHint ?? false))
                .ToList();
            return result;
        }, ct);

    public Task<McpCallResult> CallToolAsync(
        McpServer server, string toolName, JsonElement arguments, CancellationToken ct)
        => WithClientAsync(server, async (client, opCt) =>
        {
            var callParams = new CallToolRequestParams { Name = toolName, Arguments = ToArguments(arguments) };
            var result = await client.CallToolAsync(callParams, opCt);
            return Normalize(result);
        }, ct);

    private async Task<T> WithClientAsync<T>(
        McpServer server, Func<SdkClient, CancellationToken, Task<T>> op, CancellationToken ct)
    {
        var conn = await _factory.CreateAsync(server, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(OperationTimeout);

        var httpClient = _httpFactory.CreateClient(
            conn.TlsSkipVerify ? McpHttpClients.Insecure : McpHttpClients.Secure);

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = conn.Endpoint,
            TransportMode = HttpTransportMode.AutoDetect,
            ConnectionTimeout = ConnectionTimeout,
            Name = server.Name,
        };
        if (conn.Headers.Count > 0) transportOptions.AdditionalHeaders = conn.Headers;

        var transport = new HttpClientTransport(transportOptions, httpClient, _loggerFactory, ownsHttpClient: false);

        SdkClient client;
        try
        {
            client = await SdkClient.CreateAsync(transport, ClientOptions, _loggerFactory, cts.Token);
        }
        catch
        {
            // CreateAsync failed before the client took ownership of the transport,
            // so nothing else will dispose it.
            await transport.DisposeAsync();
            throw;
        }

        await using (client)
        {
            return await op(client, cts.Token);
        }
    }

    internal static McpCallResult Normalize(CallToolResult result)
    {
        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is not TextContentBlock text) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text.Text);
        }

        var content = sb.ToString();
        if (content.Length > MaxContentChars)
            content = content[..MaxContentChars] + "\n…(truncated)";

        JsonElement? structured = result.StructuredContent is { } s ? s.Clone() : null;
        return new McpCallResult(content, structured, result.IsError ?? false);
    }

    internal static Dictionary<string, JsonElement>? ToArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in arguments.EnumerateObject()) dict[p.Name] = p.Value;
        return dict;
    }
}
