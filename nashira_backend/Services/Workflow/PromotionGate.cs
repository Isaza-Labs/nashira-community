namespace nashira_backend.Services.Workflow;

public sealed record GateSimulation(string? SchemaHash, bool Ok);

public sealed record GateResult(int HttpStatus, string Code, string Message)
{
    public bool Ok => HttpStatus == 200;
}

// Pure promotion-gate state machine (contract-visible; the gate conformance family runs
// against it). Path: draft -> qa -> production. draft->qa requires a simulation that is
// present, ok, and not stale (its SchemaHash equals the workflow's current hash) — the
// 412 codes are reified from the oracle. qa->production approval is checked by the
// service (needs request data beyond this state machine).
public static class PromotionGate
{
    public const string CodeOk = "ok";
    public const string CodeInvalidTransition = "invalid_transition";
    public const string CodeSimulationMissing = "simulation_missing";
    public const string CodeSimulationFailed = "simulation_failed";
    public const string CodeSimulationStale = "simulation_stale";

    public static GateResult Check(string action, string workflowState, GateSimulation? simulation, string? currentSchemaHash)
    {
        var (from, to) = ParseAction(action);
        if (from is null || to is null)
            return new GateResult(400, CodeInvalidTransition, $"unrecognized promotion action '{action}'");

        if (!string.Equals(workflowState, from, StringComparison.Ordinal))
            return new GateResult(400, CodeInvalidTransition,
                $"workflow is in '{workflowState}', cannot apply '{action}'");

        var validStep = (from, to) is ("draft", "qa") or ("qa", "production");
        if (!validStep)
            return new GateResult(400, CodeInvalidTransition, $"'{from}' -> '{to}' is not a promotion step");

        if (to == "qa")
        {
            if (simulation is null)
                return new GateResult(412, CodeSimulationMissing,
                    "draft->qa requires a successful simulation; run simulate first");
            if (!simulation.Ok)
                return new GateResult(412, CodeSimulationFailed,
                    "the last simulation found structural issues; fix them and re-simulate");
            if (!string.Equals(simulation.SchemaHash, currentSchemaHash, StringComparison.OrdinalIgnoreCase))
                return new GateResult(412, CodeSimulationStale,
                    "the workflow changed since the last simulation; re-simulate");
        }

        return new GateResult(200, CodeOk, string.Empty);
    }

    private static (string? From, string? To) ParseAction(string? action)
    {
        // "promote:draft->qa"
        if (string.IsNullOrWhiteSpace(action) || !action.StartsWith("promote:", StringComparison.Ordinal))
            return (null, null);
        var parts = action["promote:".Length..].Split("->", 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0 ? (parts[0], parts[1]) : (null, null);
    }
}
