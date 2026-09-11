using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Security;
using nashira_backend.Services.Ssh;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Worker.Handlers;

// Runs CLI commands on an inventory device over SSH.
//
// A thin adapter, not a port: Nashira already owns the hard parts — the Python
// netmiko runner (ISshCommandRunner), the destructive-command policy, host-key
// pinning. This wires those to a workflow step and adds the one thing the agent
// path did not need: enforcing the run's environment against the device's allow
// trio, which is why the environment travels on SnippetRequest.
//
// Input keys follow workflow.v1 snippets/SPEC.md, canonical and alias alike:
//   commands (C) / command (A: one string)
//   device (C: inventory name or IP) / host (A: IP, resolved to an inventory
//     device — never an ad-hoc connection; no match is `not_found`)
//   credential (C: credential NAME, overrides the device's own)
//   username, password, private_key, key_passphrase, enable_secret / enable —
//     secret material is accepted only as a ${secret:…} reference, resolved right
//     before the wire. A plain value is refused: it is not portable, it would land
//     in the stored input snapshot, and the credential store exists so it never
//     has to be typed into a workflow.
//   use_structured (C) / structured (A), stop_on_error, port, timeout_seconds,
//     device_type
//
// NonReversible by default — snippets/SPEC.md fixes `ssh` at `non_reversible`, and a
// handler floor is the one direction a snippet may not walk back (execution/SPEC.md
// §2). A `show` is a read, but the handler cannot prove that from the command text
// alone, and the cost of guessing wrong is a config push sitting in a rollback plan
// that promises a reversal nobody can perform. The price of the safe reading is that
// an ssh step is never auto-retried and never counted reversible.
public sealed partial class SshSnippetHandler : ISnippetHandler
{
    [GeneratedRegex(@"^\s*\$\{secret:[^}]+\}\s*$", RegexOptions.Compiled)]
    private static partial Regex SecretReference();

    private readonly AppDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly ISecretResolver _secrets;
    private readonly ISshCommandRunner _runner;
    private readonly ISshCommandPolicy _policy;
    private readonly ILogger<SshSnippetHandler> _logger;

    public SshSnippetHandler(
        AppDbContext db, ISecretProtector protector, ISecretResolver secrets, ISshCommandRunner runner,
        ISshCommandPolicy policy, ILogger<SshSnippetHandler> logger)
    {
        _db = db;
        _protector = protector;
        _secrets = secrets;
        _runner = runner;
        _policy = policy;
        _logger = logger;
    }

    public string Type => Data.Models.Snippet.TypeSsh;
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.NonReversible;

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var input = request.Input;
        var deviceRef = Str(input, "device");
        var hostRef = Str(input, "host");
        if (string.IsNullOrWhiteSpace(deviceRef) && string.IsNullOrWhiteSpace(hostRef))
            return SnippetResult.Fail("ssh needs a `device` (inventory name or IP) or a `host` (IP)", "bad_input");

        var commands = Commands(input);
        if (commands.Count == 0)
            return SnippetResult.Fail("ssh needs a non-empty `commands` array (or a `command`)", "bad_input");

        var policy = _policy.Evaluate(commands);
        if (!policy.Allowed)
            return SnippetResult.Fail(
                "blocked by SSH command policy — destructive command(s) not permitted: " +
                string.Join(", ", policy.Blocked), "policy_blocked");

        // `device` matches by name or IP; `host` is an address and matches by IP
        // only. Either way the target must be in inventory — the environment gate
        // and the host-key pin live on the device row, and a workflow must not be
        // able to reach a machine that has neither.
        Device? device;
        if (!string.IsNullOrWhiteSpace(deviceRef))
        {
            device = await _db.Devices.FirstOrDefaultAsync(
                d => d.IsActive && (d.DeviceName == deviceRef || d.IpAddress == deviceRef), ct);
            if (device is null)
                return SnippetResult.Fail($"device '{deviceRef}' is not in inventory", "not_found");
        }
        else
        {
            var ip = hostRef!.Trim();
            device = await _db.Devices.FirstOrDefaultAsync(d => d.IsActive && d.IpAddress == ip, ct);
            if (device is null)
                return SnippetResult.Fail(
                    $"host '{ip}' does not match any inventory device by IP — register it first", "not_found");
        }

        // The promotion-stage gate. Unlike the agent path, a workflow step always
        // has an environment, so this always applies.
        if (DeviceEnvironmentPolicy.Refusal(device, request.Environment) is { } refusal)
        {
            _logger.LogWarning("workflow.ssh.environment_refused device={Device} environment={Environment}",
                device.DeviceName, request.Environment);
            return SnippetResult.Fail(refusal, "environment_denied");
        }

        // Secret material from the payload. Only ${secret:…} references are
        // accepted; the values are resolved here and nowhere else.
        string? username = Str(input, "username");
        string? password = null, privateKey = null, keyPassphrase = null, enableSecret = null;
        try
        {
            username = await ResolveIfReferenceAsync(username, ct);
            password = await ResolveSecretAsync(input, "password", ct);
            privateKey = await ResolveSecretAsync(input, "private_key", ct);
            keyPassphrase = await ResolveSecretAsync(input, "key_passphrase", ct);
            enableSecret = await ResolveSecretAsync(input, "enable_secret", ct);
            // `enable` is the alias: a boolean means "use the password", a string
            // is a reference like enable_secret.
            if (enableSecret is null && input.ValueKind == JsonValueKind.Object
                && input.TryGetProperty("enable", out var en) && en.ValueKind == JsonValueKind.String)
                enableSecret = await ResolveSecretAsync(input, "enable", ct);
        }
        catch (SecretInputException ex)
        {
            return SnippetResult.Fail(ex.Message, ex.Code);
        }

        // The credential: named in the payload, else the device's own. Skipped
        // entirely when the payload supplied both the username and the material.
        var payloadHasMaterial = !string.IsNullOrEmpty(password) || !string.IsNullOrEmpty(privateKey);
        Credential? credential = null;
        if (!(payloadHasMaterial && !string.IsNullOrWhiteSpace(username)))
        {
            var credentialName = Str(input, "credential");
            if (!string.IsNullOrWhiteSpace(credentialName))
            {
                credential = await _db.Credentials.FirstOrDefaultAsync(
                    c => c.IsActive && c.Name == credentialName, ct);
                if (credential is null)
                    return SnippetResult.Fail($"no credential named '{credentialName}'", "not_found");
            }
            else
            {
                if (device.CredentialId is null)
                    return SnippetResult.Fail($"device '{device.DeviceName}' has no credential assigned", "no_credential");
                credential = await _db.Credentials.FirstOrDefaultAsync(
                    c => c.IsActive && c.CredentialId == device.CredentialId, ct);
                if (credential is null)
                    return SnippetResult.Fail($"the credential for '{device.DeviceName}' is missing or inactive", "no_credential");
            }

            if (credential.AuthMethod is not (Credential.AuthMethodPassword or Credential.AuthMethodKey))
                return SnippetResult.Fail(
                    $"credential '{credential.Name}' is '{credential.AuthMethod}' — device SSH needs 'password' or 'key'",
                    "bad_credential");

            if (string.IsNullOrWhiteSpace(username)) username = credential.Username;
            if (string.IsNullOrWhiteSpace(username))
                return SnippetResult.Fail($"credential '{credential.Name}' has no username", "bad_credential");

            if (!payloadHasMaterial)
            {
                try
                {
                    if (credential.AuthMethod == Credential.AuthMethodKey)
                    {
                        privateKey = _protector.Decrypt(credential.EncryptedPrivateKey);
                        if (string.IsNullOrEmpty(privateKey))
                            return SnippetResult.Fail($"credential '{credential.Name}' is key-based but has no key", "bad_credential");
                        keyPassphrase ??= _protector.Decrypt(credential.EncryptedKeyPassphrase);
                    }
                    else
                    {
                        password = _protector.Decrypt(credential.EncryptedPassword);
                        if (string.IsNullOrEmpty(password))
                            return SnippetResult.Fail($"credential '{credential.Name}' has no password", "bad_credential");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "workflow.ssh.decrypt_failed credential={Credential}", credential.Name);
                    return SnippetResult.Fail("failed to decrypt the device credential", "decrypt_failed");
                }
            }
        }

        if (enableSecret is null && Bool(input, "enable", false)) enableSecret = password;

        var outcome = await _runner.RunAsync(new SshRunRequest
        {
            Host = device.IpAddress,
            Port = Int(input, "port", 22),
            Username = username!,
            Password = password,
            PrivateKey = privateKey,
            KeyPassphrase = keyPassphrase,
            EnableSecret = enableSecret,
            DeviceType = Str(input, "device_type")
                ?? (string.IsNullOrWhiteSpace(device.Platform) ? null : device.Platform),
            Commands = commands,
            StopOnError = Bool(input, "stop_on_error", true),
            UseStructured = Bool(input, "use_structured", Bool(input, "structured", true)),
            TimeoutSeconds = Int(input, "timeout_seconds", 30),
            ExpectedFingerprint = device.ExpectedSshHostKeyFingerprint,
        }, ct);

        if (!outcome.Ok || outcome.Result is null)
        {
            _logger.LogWarning("workflow.ssh.failed device={Device} kind={Kind}",
                device.DeviceName, outcome.ErrorKind);
            return new SnippetResult
            {
                Success = false,
                // The commands live on the NODE, not on this snippet, so the same snippet
                // runs `show version` on one node and `configure terminal` on another. This
                // handler sees text going over a channel and output coming back; it cannot
                // tell which of those two it just did. The node declares (D1).
                Change = StepChange.AuthorDecides,
                Output = JsonSerializer.SerializeToElement(new
                {
                    device = device.DeviceName,
                    host = device.IpAddress,
                    error = outcome.Error ?? "SSH execution failed",
                    error_kind = outcome.ErrorKind,
                }),
                Error = outcome.Error ?? "SSH execution failed",
                ErrorCode = outcome.ErrorKind ?? "ssh_failed",
                // A transport failure may be transient; a rejected key will not fix
                // itself on a retry.
                Retryable = outcome.ErrorKind is not ("auth_failed" or "host_key_mismatch"),
            };
        }

        var result = outcome.Result;
        await PinHostKeyOnFirstSeenAsync(device, result, ct);

        var rows = result.Results.Select(r => new
        {
            command = r.Command,
            ok = r.Ok,
            output = r.Output,
            error = r.Error,
            elapsed_ms = r.ElapsedMs,
            parsed = r.Parsed,
        }).ToList();

        var allOk = rows.All(r => r.ok);
        var failedRows = rows.Where(r => !r.ok).ToList();
        return new SnippetResult
        {
            Success = allOk,
                // The commands live on the NODE, not on this snippet, so the same snippet
                // runs `show version` on one node and `configure terminal` on another. This
                // handler sees text going over a channel and output coming back; it cannot
                // tell which of those two it just did. The node declares (D1).
                Change = StepChange.AuthorDecides,
            Output = JsonSerializer.SerializeToElement(new
            {
                device = device.DeviceName,
                host = device.IpAddress,
                device_type = result.DeviceType,
                host_key_fingerprint = result.HostKeyFingerprint,
                results = rows,
                // The contract's portable pair beside `results`: every command's
                // output in order, and an exit code a CLI session does not have.
                stdout = string.Join("\n", rows.Select(r => r.output ?? string.Empty)),
                exit_code = (int?)null,
            }),
            // "one or more commands failed" sent the reader into the payload to find
            // out which one and why, on a step whose whole point is the commands. Name
            // the first failure and say how many followed it.
            Error = allOk
                ? string.Empty
                : $"`{failedRows[0].command}` failed on {device.DeviceName}: "
                  + $"{Summarize(failedRows[0].error ?? failedRows[0].output)}"
                  + (failedRows.Count > 1 ? $" (and {failedRows.Count - 1} more command(s))" : string.Empty),
            ErrorCode = allOk ? null : "command_failed",
            Logs = $"{device.DeviceName} ({device.IpAddress}, {result.DeviceType}): "
                   + $"ran {rows.Count} command(s), {failedRows.Count} failed, "
                   + $"{rows.Sum(r => r.elapsed_ms)}ms total",
        };
    }

    private sealed class SecretInputException(string message, string code) : Exception(message)
    {
        public string Code { get; } = code;
    }

    // A secret-bearing key: absent → null; a ${secret:…} reference → its value;
    // anything else → refused. The reference must resolve, too — an unresolved
    // marker sent as a password is a guaranteed auth failure with a misleading
    // cause.
    private async Task<string?> ResolveSecretAsync(JsonElement input, string key, CancellationToken ct)
    {
        var raw = Str(input, key);
        if (string.IsNullOrEmpty(raw)) return null;
        if (!SecretReference().IsMatch(raw))
            throw new SecretInputException(
                $"`{key}` must be a ${{secret:...}} reference, not a plain value — store it as a credential or secret",
                "bad_input");
        var resolved = await _secrets.SubstituteAsync(raw.Trim(), ct);
        if (resolved.Contains("${secret:", StringComparison.Ordinal))
            throw new SecretInputException($"`{key}` references a secret that does not resolve: {raw.Trim()}",
                "secret_unresolved");
        return resolved;
    }

    // `username` is not secret material, so a plain value is fine — but a reference
    // (`${secret:credential:x:username}`) is resolved like any other.
    private async Task<string?> ResolveIfReferenceAsync(string? value, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(value) || !SecretReference().IsMatch(value)) return value;
        var resolved = await _secrets.SubstituteAsync(value.Trim(), ct);
        if (resolved.Contains("${secret:", StringComparison.Ordinal))
            throw new SecretInputException($"`username` references a secret that does not resolve: {value.Trim()}",
                "secret_unresolved");
        return resolved;
    }

    // Device output is unbounded and a failure line is usually the first thing in it.
    // Enough to recognise the failure without putting a screen of banner text in the
    // step's error column — the full text is in the output payload.
    private static string Summarize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "no output";
        var line = text.ReplaceLineEndings(Environment.NewLine)
                       .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                       .Select(l => l.Trim())
                       .FirstOrDefault(l => l.Length > 0)
                   ?? text.Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }

    // Trust-on-first-use, same as the agent path: pin what we saw so later
    // connects are verified against it.
    private async Task PinHostKeyOnFirstSeenAsync(Device device, SshRunResult result, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(device.ExpectedSshHostKeyFingerprint)
            || string.IsNullOrEmpty(result.HostKeyFingerprint))
            return;

        device.ExpectedSshHostKeyFingerprint = result.HostKeyFingerprint;
        device.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("workflow.ssh.host_key_pinned device={Device}", device.DeviceName);
    }

    // `commands` (array or lone string) or `command` (one string). Public so a
    // caller normalising a node can reuse the rule.
    internal static List<string> Commands(JsonElement input)
    {
        var list = new List<string>();
        if (input.ValueKind != JsonValueKind.Object) return list;
        if (!input.TryGetProperty("commands", out var c) && !input.TryGetProperty("command", out c)) return list;

        // Accept a lone string too: a one-command step should not need an array.
        if (c.ValueKind == JsonValueKind.String)
        {
            var single = c.GetString();
            if (!string.IsNullOrWhiteSpace(single)) list.Add(single!);
            return list;
        }
        if (c.ValueKind != JsonValueKind.Array) return list;

        foreach (var el in c.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String) continue;
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
        }
        return list;
    }

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int Int(JsonElement e, string k, int def) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : def;

    private static bool Bool(JsonElement e, string k, bool def) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;
}
