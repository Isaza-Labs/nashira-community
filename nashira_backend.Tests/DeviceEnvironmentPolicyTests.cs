using nashira_backend.Services.Workflow;
using DeviceEntity = nashira_backend.Data.Models.Device;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Tests;

// Which promotion stage may dispatch to which device. The three flags are
// independent, so the interesting cases are the ones that are not "all on".
public class DeviceEnvironmentPolicyTests
{
    private static DeviceEntity Device(bool draft = true, bool qa = false, bool prod = true) => new()
    {
        DeviceId = Guid.NewGuid(),
        DeviceName = "edge-01",
        AllowDraft = draft,
        AllowQa = qa,
        AllowProduction = prod,
    };

    [Fact]
    public void Defaults_allow_draft_and_production_but_not_qa()
    {
        var d = Device();
        Assert.True(DeviceEnvironmentPolicy.Allows(d, WorkflowEntity.EnvDraft));
        Assert.False(DeviceEnvironmentPolicy.Allows(d, WorkflowEntity.EnvQa));
        Assert.True(DeviceEnvironmentPolicy.Allows(d, WorkflowEntity.EnvProduction));
    }

    [Fact]
    public void A_null_environment_means_no_workflow_run_so_nothing_is_enforced()
    {
        // Agent chat and direct API calls go through the same handler; the trio is
        // about promotion stages, and outside a run there is no stage to check.
        Assert.Null(DeviceEnvironmentPolicy.Refusal(Device(draft: false, prod: false), null));
        Assert.Null(DeviceEnvironmentPolicy.Refusal(Device(draft: false, prod: false), ""));
    }

    [Fact]
    public void Refusal_names_the_environments_the_device_does_allow()
    {
        var refusal = DeviceEnvironmentPolicy.Refusal(
            Device(draft: true, qa: false, prod: false), WorkflowEntity.EnvProduction);

        Assert.NotNull(refusal);
        Assert.Contains("production", refusal);
        Assert.Contains("draft", refusal);
    }

    [Fact]
    public void A_device_that_allows_nothing_is_reported_as_parked()
    {
        var refusal = DeviceEnvironmentPolicy.Refusal(
            Device(draft: false, qa: false, prod: false), WorkflowEntity.EnvDraft);

        Assert.NotNull(refusal);
        Assert.Contains("not reachable from any environment", refusal);
    }

    [Fact]
    public void An_unknown_environment_fails_closed()
    {
        // Fail-open here would turn a typo in a new environment name into an
        // unrestricted dispatch to every device.
        var d = Device(draft: true, qa: true, prod: true);
        Assert.False(DeviceEnvironmentPolicy.Allows(d, "staging"));
        Assert.NotNull(DeviceEnvironmentPolicy.Refusal(d, "staging"));
    }

    [Fact]
    public void Scope_restores_the_previous_environment_on_dispose()
    {
        var scope = new WorkflowExecutionScope();
        Assert.Null(scope.Environment);

        using (scope.Enter(WorkflowEntity.EnvQa))
        {
            Assert.Equal(WorkflowEntity.EnvQa, scope.Environment);
            using (scope.Enter(WorkflowEntity.EnvProduction))
                Assert.Equal(WorkflowEntity.EnvProduction, scope.Environment);
            Assert.Equal(WorkflowEntity.EnvQa, scope.Environment);
        }

        Assert.Null(scope.Environment);
    }
}
