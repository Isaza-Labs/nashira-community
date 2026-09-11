using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// The promotion-gate state machine (contract-visible; also exercised by the gate
// conformance family): draft->qa simulation gate codes + transition validation.
public class PromotionGateTests
{
    [Fact]
    public void Draft_to_qa_without_simulation_is_412_missing()
    {
        var r = PromotionGate.Check("promote:draft->qa", "draft", null, "H1");
        Assert.Equal(412, r.HttpStatus);
        Assert.Equal(PromotionGate.CodeSimulationMissing, r.Code);
    }

    [Fact]
    public void Draft_to_qa_with_failed_simulation_is_412_failed()
    {
        var r = PromotionGate.Check("promote:draft->qa", "draft", new GateSimulation("H1", Ok: false), "H1");
        Assert.Equal(412, r.HttpStatus);
        Assert.Equal(PromotionGate.CodeSimulationFailed, r.Code);
    }

    [Fact]
    public void Draft_to_qa_with_stale_simulation_is_412_stale()
    {
        var r = PromotionGate.Check("promote:draft->qa", "draft", new GateSimulation("H_old", Ok: true), "H_new");
        Assert.Equal(412, r.HttpStatus);
        Assert.Equal(PromotionGate.CodeSimulationStale, r.Code);
    }

    [Fact]
    public void Draft_to_qa_with_fresh_ok_simulation_passes()
    {
        var r = PromotionGate.Check("promote:draft->qa", "draft", new GateSimulation("H1", Ok: true), "H1");
        Assert.True(r.Ok);
        Assert.Equal(PromotionGate.CodeOk, r.Code);
    }

    [Fact]
    public void Qa_to_production_has_no_simulation_gate()
    {
        var r = PromotionGate.Check("promote:qa->production", "qa", null, "H1");
        Assert.True(r.Ok);
    }

    [Theory]
    [InlineData("promote:draft->production", "draft")] // skipping qa
    [InlineData("promote:qa->draft", "qa")]            // backwards
    [InlineData("promote:draft->qa", "qa")]            // state mismatch
    [InlineData("nonsense", "draft")]
    public void Invalid_transitions_are_400(string action, string state)
    {
        var r = PromotionGate.Check(action, state, new GateSimulation("H1", true), "H1");
        Assert.Equal(400, r.HttpStatus);
        Assert.Equal(PromotionGate.CodeInvalidTransition, r.Code);
    }
}
