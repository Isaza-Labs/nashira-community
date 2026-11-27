using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Services.Ssh;

// Spawns `python3 nashira_ssh_runner.py`, writes the request JSON to stdin, reads
// stdout/stderr, and parses the result. Enforces a (timeout + grace) process
// deadline and kills the tree on timeout/cancellation. Stateless singleton.
//
// Which python3: the image's (Python:Executable) unless the request names a Python
// environment (Device.PythonEnvironmentId), in which case that environment's
// interpreter — its own netmiko/paramiko, built with uv. And when an attempt fails
// because paramiko refuses the device's host key algorithm (paramiko 5 dropped
// `ssh-rsa`), it is retried once under the environment flagged as the SSH fallback;
// see SshEnvironmentFallback. Every outcome says which environment produced it.
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
    private readonly IPythonEnvironmentResolver? _environments;

    public SshCommandRunner(
        IConfiguration config, ILogger<SshCommandRunner> logger, IPythonEnvironmentResolver? environments = null)
    {
        _executable = config["Python:Executable"] ?? DefaultExecutable;
        _runnerPath = config["Python:SshRunnerPath"] ?? DefaultRunnerPath;
        _logger = logger;
        _environments = environments;
    }

    public async Task<SshRunOutcome> RunAsync(SshRunRequest request, CancellationToken ct)
    {
        var executable = _executable;
        var environment = Data.Models.PythonEnvironment.SystemName;

        if (request.PythonEnvironmentId is { } environmentId)
        {
            if (_environments is null)
                return SshRunOutcome.Fail(PythonEnvironmentUnavailableException.Code,
                    "Python environments are not available in this process");
            try
            {
                var env = await _environments.ResolveAsync(environmentId, ct);
                executable = env.Interpreter;
                environment = env.Name;
            }
            catch (PythonEnvironmentUnavailableException ex)
            {
                // Refused rather than quietly run under the system Python: the device was
                // pointed at this environment because the system one does not work for it.
                _logger.LogWarning("ssh.runner.python_env_unavailable host={Host} reason={Reason}",
                    request.Host, ex.Message);
                return SshRunOutcome.Fail(PythonEnvironmentUnavailableException.Code, ex.Message);
            }
        }

        var outcome = (await RunOnceAsync(request, executable, ct)).WithEnvironment(environment);

        if (_environments is null || !SshEnvironmentFallback.IsIncompatibleHostKey(outcome))
            return outcome;

        var fallback = await _environments.FindSshFallbackAsync(ct);
        if (!SshEnvironmentFallback.ShouldRetry(outcome, request.PythonEnvironmentId, fallback))
            return outcome;

        var originalError = outcome.Error
            ?? outcome.Result?.Results.Select(r => r.Error).FirstOrDefault(e => !string.IsNullOrEmpty(e))
            ?? "incompatible ssh peer";
        _logger.LogWarning(
            "ssh.runner.env_fallback host={Host} from={From} to={To} error={Error}",
            request.Host, environment, fallback!.Name, originalError);

        var retried = await RunOnceAsync(request, fallback.Interpreter, ct);
        return retried.WithEnvironment(fallback.Name, fallbackFrom: originalError);
    }

    private async Task<SshRunOutcome> RunOnceAsync(SshRunRequest request, string executable, CancellationToken ct)
    {
        var payload = SerializeRequest(request);

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(_runnerPath);
        // The runner imports its sibling modules (nashira_ssh_parsers, nashira_ssh_clean)
        // from its own directory. The image sets PYTHONPATH to it; set it here too so an
        // environment's interpreter — which shares nothing else with the image's — finds
        // them regardless of what the process environment says.
        var runnerDir = Path.GetDirectoryName(_runnerPath);
        if (!string.IsNullOrEmpty(runnerDir))
        {
            var inherited = Environment.GetEnvironmentVariable("PYTHONPATH");
            psi.Environment["PYTHONPATH"] = string.IsNullOrEmpty(inherited)
                ? runnerDir
                : inherited.Split(Path.PathSeparator).Contains(runnerDir)
                    ? inherited
                    : runnerDir + Path.PathSeparator + inherited;
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ssh.runner.unavailable exe={Exe} runner={Runner}", executable, _runnerPath);
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
