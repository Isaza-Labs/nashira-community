using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Worker.Handlers;

// Stub — NETCONF over SSH (RFC 6241) is planned but not implemented, mirroring
// FlowWeaver. The real handler needs an XML-aware NETCONF client: a library, or a
// hand-rolled SSH-subsystem client that frames messages with `]]>]]>` and handles
// <hello>/<rpc>/<rpc-reply>/<notification>.
//
// The stub stays registered so a workflow (or an imported bundle) that references
// type `netconf` fails with a clear, actionable message instead of
// `unknown_handler` — and so the type shows up in /snippets/types as reserved.
// Deliberately no baseline snippet is seeded for it: a seeded row that can only
// fail would put a booby trap in the catalogue.
public sealed class NetconfSnippetHandler : ISnippetHandler
{
    public string Type => Data.Models.Snippet.TypeNetconf;

    // NETCONF operations modify device config; commit-confirmed flows require an
    // explicit confirm/discard edge.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.RequiresCompensation;

    private readonly ILogger<NetconfSnippetHandler> _logger;

    public NetconfSnippetHandler(ILogger<NetconfSnippetHandler> logger) => _logger = logger;

    public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        _logger.LogError("workflow.netconf.failed node={Node} reason={Reason}", request.NodeId, "not_implemented");
        return Task.FromResult(SnippetResult.Fail(
            "the netconf handler is not implemented yet — it needs a NETCONF client dependency. "
            + "Use the ssh snippet for CLI-driven changes in the meantime.",
            "not_implemented"));
    }
}
