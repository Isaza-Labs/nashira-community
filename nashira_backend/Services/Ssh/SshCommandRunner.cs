using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Services.Ssh;

// Spawns `python3 nashira_ssh_runner.py`, writes the request JSON to stdin, reads
// stdout/stderr, and parses the result. Enforces a (timeout + grace) process
// deadline and kills the tree on timeout/cancellation. Stateless singleton.
public sealed class SshCommandRunner : ISshCommandRunner
{
    private const string DefaultExecutable = "python3";
    private const string DefaultRunnerPath = "/usr/local/lib/nashira_python/nashira_ssh_runner.py";
    private const int GraceSeconds = 30; // netmiko import + first handshake headroom

    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions DeserializeOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _executable;
    private readonly string _runnerPath;
    private readonly ILogger<SshCommandRunner> _logger;

    public SshCommandRunner(IConfiguration config, ILogger<SshCommandRunner> logger)
    {
        _executable = config["Python:Executable"] ?? DefaultExecutable;
        _runnerPath = config["Python:SshRunnerPath"] ?? DefaultRunnerPath;
        _logger = logger;
    }

    public async Task<SshRunOutcome> RunAsync(SshRunRequest request, CancellationToken ct)
    {
        var payload = SerializeRequest(request);

        var psi = new ProcessStartInfo
        {
            FileName = _executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(_runnerPath);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ssh.runner.unavailable exe={Exe} runner={Runner}", _executable, _runnerPath);
            return SshRunOutcome.Fail("runner_unavailable", $"SSH runtime not available: {ex.Message}");
        }

        var deadline = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 5, 300) + GraceSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);

        try
        {
            await proc.StandardInput.WriteAsync(payload.AsMemory(), cts.Token);
            proc.StandardInput.Close();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode != 0)
            {
                var kind = ClassifyExit(proc.ExitCode);
                _logger.LogWarning("ssh.runner.exit code={Code} kind={Kind} host={Host}", proc.ExitCode, kind, request.Host);
                var msg = string.IsNullOrWhiteSpace(stderr) ? $"runner exit {proc.ExitCode}" : stderr.Trim();
                return SshRunOutcome.Fail(kind, msg, proc.ExitCode);
            }

            var result = ParseResult(stdout);
            return result is null
                ? SshRunOutcome.Fail("bad_output", "runner produced no parseable output")
                : SshRunOutcome.Success(result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(proc);
            _logger.LogWarning("ssh.runner.timeout host={Host} deadline={Deadline}s", request.Host, (int)deadline.TotalSeconds);
            return SshRunOutcome.Fail("timeout", $"SSH runner timed out after {(int)deadline.TotalSeconds}s");
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw; // caller cancelled
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "ssh.runner.bad_output host={Host}", request.Host);
            return SshRunOutcome.Fail("bad_output", "runner output was not valid JSON");
        }
        catch (Exception ex)
        {
            TryKill(proc);
            _logger.LogError(ex, "ssh.runner.error host={Host}", request.Host);
            return SshRunOutcome.Fail("runner_error", ex.Message);
        }
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // best effort — process may have already exited
        }
    }

    // Contract seams — the exact serialization/parsing/classification RunAsync uses,
    // exposed to the test project (InternalsVisibleTo) so the runner JSON contract is
    // covered without spawning a real Python subprocess.
    internal static string SerializeRequest(SshRunRequest request) =>
        JsonSerializer.Serialize(request, SerializeOpts);

    internal static SshRunResult? ParseResult(string json) =>
        JsonSerializer.Deserialize<SshRunResult>(json, DeserializeOpts);

    internal static string ClassifyExit(int code) => code switch
    {
        1 => "input_error",
        2 => "auth_failed",
        3 => "connect_timeout",
        4 => "host_key_mismatch",
        5 => "connect_failed",
        99 => "runner_exception",
        _ => "unknown",
    };
}
