using System.Diagnostics;
using nashira_backend.Configuration.Modules;

namespace nashira_backend.Tests;

public class ModuleStartupTests
{
    [Theory]
    [InlineData(null, "DefaultAll")]
    [InlineData("chat,ai-studio,integrations,secrets", "Explicit")]
    public async Task Valid_selection_is_resolved_before_later_startup_validation(
        string? configuredModules,
        string expectedMode)
    {
        var result = await StartBackendAsync(configuredModules);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            $"deployment.modules.resolved mode={expectedMode}",
            result.Output);
        Assert.Contains("Jwt:Key", result.Output);
        Assert.DoesNotContain("Module configuration is invalid", result.Output);
    }

    [Theory]
    [InlineData("", "NASHIRA_MODULES is defined but empty")]
    [InlineData("chat,secrets", "module \"chat\" requires module \"ai-studio\"")]
    [InlineData("ai-studio", "module \"ai-studio\" requires module \"integrations\"")]
    public async Task Invalid_selection_stops_the_process_before_other_startup_validation(
        string configuredModules,
        string expectedError)
    {
        var result = await StartBackendAsync(configuredModules);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Module configuration is invalid", result.Output);
        Assert.Contains(expectedError, result.Output);
        Assert.DoesNotContain("Jwt:Key", result.Output);
    }

    private static async Task<ProcessResult> StartBackendAsync(string? configuredModules)
    {
        var backendAssembly = typeof(ModuleSelection).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(backendAssembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(backendAssembly);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["Jwt__Key"] = string.Empty;
        if (configuredModules is null)
            startInfo.Environment.Remove(ModuleSelection.EnvironmentVariableName);
        else
            startInfo.Environment[ModuleSelection.EnvironmentVariableName] = configuredModules;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the backend process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The backend did not finish its startup smoke test in time.");
        }

        return new ProcessResult(
            process.ExitCode,
            string.Concat(await stdout, Environment.NewLine, await stderr));
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
