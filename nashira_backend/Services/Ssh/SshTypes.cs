using System.Text.Json;

namespace nashira_backend.Services.Ssh;

// Input handed to the Python netmiko runner. Credentials are already decrypted;
// this object is serialized to the runner's snake_case stdin contract.
public sealed class SshRunRequest
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }
    public string? Password { get; init; }
    public string? PrivateKey { get; init; }
    public string? KeyPassphrase { get; init; }
    public string? EnableSecret { get; init; }
    public string? DeviceType { get; init; }
    public required IReadOnlyList<string> Commands { get; init; }
    public bool StopOnError { get; init; } = true;
    public int TimeoutSeconds { get; init; } = 30;
    public bool UseStructured { get; init; }
    public bool PreserveAnsi { get; init; }
    public bool? DirectExec { get; init; }
    public string? ExpectedFingerprint { get; init; }
}

// One command's result inside SshRunResult.Results (runner snake_case output).
public sealed class SshCommandResult
{
    public string? Command { get; init; }
    public string? Output { get; init; }
    public string? OutputRaw { get; init; }
    public JsonElement? Parsed { get; init; }
    public string? ParserUsed { get; init; }
    public string? TemplateName { get; init; }
    public int ElapsedMs { get; init; }
    public bool Ok { get; init; }
    public string? Error { get; init; }
}

// Parsed stdout of a successful (exit 0) runner invocation.
public sealed class SshRunResult
{
    public List<SshCommandResult> Results { get; init; } = [];
    public string? DeviceType { get; init; }
    public string? DeviceTypeResolvedFrom { get; init; }
    public string? Vendor { get; init; }
    public string? HostKeyFingerprint { get; init; }
    public string? DeviceTypeFallback { get; init; }
}

// Outcome of a runner call: either a parsed result, or a classified failure.
// ErrorKind mirrors the runner's exit-code taxonomy (auth_failed, connect_timeout,
// host_key_mismatch, ...) plus C#-side kinds (timeout, runner_unavailable).
public sealed class SshRunOutcome
{
    public bool Ok { get; private init; }
    public int ExitCode { get; private init; } = -1;
    public string ErrorKind { get; private init; } = "ok";
    public string? Error { get; private init; }
    public SshRunResult? Result { get; private init; }

    public static SshRunOutcome Success(SshRunResult result) =>
        new() { Ok = true, ErrorKind = "ok", Result = result };

    public static SshRunOutcome Fail(string kind, string error, int exitCode = -1) =>
        new() { Ok = false, ErrorKind = kind, Error = error, ExitCode = exitCode };
}
