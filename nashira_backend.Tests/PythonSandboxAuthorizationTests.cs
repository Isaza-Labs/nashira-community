using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Tests;

public class PythonSandboxAuthorizationTests
{
    private static Task<PythonRunResult> Run(string? role, bool authenticated, bool optIn, string? wrapper = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Python:Executable"] = PythonSandboxTests.Exe ?? "python3",
            ["Python:AllowUnsandboxed"] = optIn.ToString(),
            ["Python:SandboxCommand"] = wrapper,
        }).Build();
        return new PythonSandbox(config, NullLogger<PythonSandbox>.Instance,
            new HardeningTestUser(role, authenticated)).RunAsync(new PythonRunRequest(
                "result = 42", JsonSerializer.SerializeToElement(new { }), 5, false, []), default);
    }

    [Theory]
    [InlineData("operator", true)]
    [InlineData("viewer", true)]
    [InlineData("Admin", true)]
    [InlineData("admin", false)]
    [InlineData(null, false)]
    public async Task Deployment_opt_in_cannot_grant_unsandboxed_execution_to_non_admins(string? role, bool authenticated)
    {
        var result = await Run(role, authenticated, true);
        Assert.False(result.Ok);
        Assert.Equal("unsandboxed_admin_required", result.ErrorKind);
    }

    [Fact]
    public async Task Admin_without_explicit_deployment_flag_is_refused()
    {
        var result = await Run("admin", true, false);
        Assert.Equal("sandbox_not_configured", result.ErrorKind);
    }

    [Fact]
    public async Task Missing_identity_fails_closed_even_with_opt_in()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Python:Executable"] = PythonSandboxTests.Exe ?? "python3",
            ["Python:AllowUnsandboxed"] = "true",
        }).Build();
        var result = await new PythonSandbox(config, NullLogger<PythonSandbox>.Instance)
            .RunAsync(new PythonRunRequest("result = 42", JsonSerializer.SerializeToElement(new { }), 5, false, []), default);
        Assert.Equal("unsandboxed_admin_required", result.ErrorKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_wrapper_cannot_use_PATH_or_fall_back(bool optIn)
    {
        var result = await Run("admin", true, optIn, "env");
        Assert.Equal("sandbox_invalid", result.ErrorKind);
    }

    [Fact]
    public async Task Missing_wrapper_cannot_fall_back_even_for_admin_with_opt_in()
    {
        var result = await Run("admin", true, true, "/nonexistent/nashira-sandbox");
        Assert.Equal("runtime_unavailable", result.ErrorKind);
    }

    [RequiresPythonFact]
    public async Task Admin_and_explicit_flag_can_run_in_disposable_test_environment()
    {
        // Skipped, with the reason shown, on a host without an interpreter.
        var result = await Run("admin", true, true);
        Assert.True(result.Ok, result.Stderr);
        Assert.Equal(42, result.Output.GetInt32());
    }
}
