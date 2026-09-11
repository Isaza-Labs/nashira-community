using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Worker.Handlers;

// Stub — SNMPv3 needs a USM-capable SNMP client (e.g. Lextm.SharpSnmpLib for
// GET/GETNEXT/WALK/SET with authPriv), which the project does not carry today.
// Mirrors FlowWeaver's stub: registered so a workflow referencing type `snmp_v3`
// fails actionably instead of `unknown_handler`, and deliberately not seeded with
// a baseline snippet — a seeded row that can only fail would put a booby trap in
// the catalogue.
public sealed class SnmpV3SnippetHandler : ISnippetHandler
{
    public string Type => Data.Models.Snippet.TypeSnmpV3;

    private readonly ILogger<SnmpV3SnippetHandler> _logger;

    public SnmpV3SnippetHandler(ILogger<SnmpV3SnippetHandler> logger) => _logger = logger;

    public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        _logger.LogError("workflow.snmp_v3.failed node={Node} reason={Reason}", request.NodeId, "not_implemented");
        return Task.FromResult(SnippetResult.Fail(
            "the snmp_v3 handler is not implemented yet — it needs a USM-capable SNMP client "
            + "(e.g. Lextm.SharpSnmpLib) wired to the device credential.",
            "not_implemented"));
    }
}
