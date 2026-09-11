using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Security;
using nashira_backend.Services.Ssh;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Runs CLI commands on a registered device over SSH via the Python netmiko runner.
// Inventory-only: the device must exist with an assigned credential — the agent
// cannot supply ad-hoc hosts or credentials. Classified execute / single_confirm.
public sealed class DeviceConnectHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "device":{"type":"string","description":"Device name (from inventory) or its IP address"},
          "commands":{"type":"array","items":{"type":"string"},"minItems":1,"description":"CLI commands to run, in order"},
          "enable":{"type":"boolean","default":false,"description":"Enter enable/privileged mode before running"},
          "structured":{"type":"boolean","default":true,"description":"Parse output into records via TextFSM when available"},
          "stop_on_error":{"type":"boolean","default":true,"description":"Stop after the first failing command"}
        },"required":["device","commands"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISecretProtector _protector;
    private readonly ISshCommandRunner _runner;
    private readonly ISshCommandPolicy _policy;
    private readonly WorkflowExecutionScope _execScope;
    private readonly ILogger<DeviceConnectHandler> _logger;

    public DeviceConnectHandler(
        AppDbContext db,
        ICurrentUser user,
        ISecretProtector protector,
        ISshCommandRunner runner,
        ISshCommandPolicy policy,
        WorkflowExecutionScope execScope,
        ILogger<DeviceConnectHandler> logger)
    {
        _db = db;
        _user = user;
        _protector = protector;
        _runner = runner;
        _policy = policy;
        _execScope = execScope;
        _logger = logger;
    }

    public string Name => "device_connect";
    public string Description =>
        "Runs one or more CLI commands on a registered device over SSH (netmiko) and returns " +
        "their output. The device must exist in inventory with a credential assigned. Prefer " +
        "read-only 'show'/'display' commands; configuration changes are higher risk.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("device", out var dEl) || dEl.ValueKind != JsonValueKind.String)
            return Err("device is required");
        var deviceRef = dEl.GetString()!.Trim();

        var commands = ReadCommands(args);
        if (commands.Count == 0)
            return Err("commands must be a non-empty array of strings");

        var policy = _policy.Evaluate(commands);
        if (!policy.Allowed)
            return Err("blocked by SSH command policy — destructive command(s) not permitted: " +
                $"{string.Join(", ", policy.Blocked)}. An admin must enable Ssh:AllowDestructiveCommands to run these.");

        var enable = Bool(args, "enable", false);
        var structured = Bool(args, "structured", true);
        var stopOnError = Bool(args, "stop_on_error", true);

        var device = await _db.Devices.FirstOrDefaultAsync(
            d => d.IsActive
                 && (d.DeviceName == deviceRef || d.IpAddress == deviceRef), ct);
        if (device is null)
            return Err($"device '{deviceRef}' is not in inventory; SSH requires a registered device with a credential");

        // Inside a workflow run, the run's environment must be one the device allows.
        // Outside one (agent chat, direct API) Environment is null and nothing is
        // enforced — this gate is about which promotion stage may reach the box, not
        // about who may reach it, which is what RBAC already covers.
        if (DeviceEnvironmentPolicy.Refusal(device, _execScope.Environment) is { } refusal)
        {
            _logger.LogWarning("device.environment.refused device={Device} environment={Environment}",
                device.DeviceName, _execScope.Environment);
            return Err(refusal);
        }

        if (device.CredentialId is null)
            return Err($"device '{device.DeviceName}' has no credential assigned");

        var credential = await _db.Credentials.FirstOrDefaultAsync(
            c => c.IsActive && c.CredentialId == device.CredentialId, ct);
        if (credential is null)
            return Err($"the credential for device '{device.DeviceName}' was not found or is inactive");
        // SSH understands password/key material only — a token/api_key/oauth2
        // credential can't open a device session, so fail with the reason instead
        // of a confusing decrypt/login error.
        if (credential.AuthMethod is not (Credential.AuthMethodPassword or Credential.AuthMethodKey))
            return Err(
                $"credential '{credential.Name}' is a '{credential.AuthMethod}' credential — device SSH needs auth_method 'password' or 'key'");
        if (string.IsNullOrWhiteSpace(credential.Username))
            return Err($"credential '{credential.Name}' has no username");

        string? password = null, privateKey = null, keyPassphrase = null;
        try
        {
            if (credential.AuthMethod == Credential.AuthMethodKey)
            {
                privateKey = _protector.Decrypt(credential.EncryptedPrivateKey);
                if (string.IsNullOrEmpty(privateKey))
                    return Err($"credential '{credential.Name}' is key-based but has no private key");
                keyPassphrase = _protector.Decrypt(credential.EncryptedKeyPassphrase);
            }
            else
            {
                password = _protector.Decrypt(credential.EncryptedPassword);
                if (string.IsNullOrEmpty(password))
                    return Err($"credential '{credential.Name}' has no password");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ssh.credential.decrypt_failed credential={Cred}", credential.Name);
            return Err("failed to decrypt the device credential");
        }

        var request = new SshRunRequest
        {
            Host = device.IpAddress,
            Username = credential.Username!,
            Password = password,
            PrivateKey = privateKey,
            KeyPassphrase = keyPassphrase,
            // No dedicated enable-secret field yet; reuse the login password when enable is requested.
            EnableSecret = enable ? password : null,
            DeviceType = string.IsNullOrWhiteSpace(device.Platform) ? null : device.Platform,
            Commands = commands,
            StopOnError = stopOnError,
            UseStructured = structured,
            ExpectedFingerprint = device.ExpectedSshHostKeyFingerprint,
        };

        var outcome = await _runner.RunAsync(request, ct);
        if (!outcome.Ok || outcome.Result is null)
        {
            _logger.LogWarning("ssh.exec.failed device={Device} kind={Kind}", device.DeviceName, outcome.ErrorKind);
            return JsonSerializer.SerializeToElement(new
            {
                error = outcome.Error ?? "SSH execution failed",
                error_kind = outcome.ErrorKind,
                device = device.DeviceName,
                host = device.IpAddress,
            });
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
            parsed = structured ? r.Parsed : null,
        });

        return JsonSerializer.SerializeToElement(new
        {
            device = device.DeviceName,
            host = device.IpAddress,
            device_type = result.DeviceType,
            device_type_fallback = result.DeviceTypeFallback,
            host_key_fingerprint = result.HostKeyFingerprint,
            results = rows,
        });
    }

    // Trust-on-first-use: pin the SSH host key the first time we see it so later
    // connects are verified against it (the runner enforces the pin server-side).
    private async Task PinHostKeyOnFirstSeenAsync(Device device, SshRunResult result, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(device.ExpectedSshHostKeyFingerprint)
            || string.IsNullOrEmpty(result.HostKeyFingerprint))
            return;

        device.ExpectedSshHostKeyFingerprint = result.HostKeyFingerprint;
        device.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("ssh.host_key.first_seen device={Device} fingerprint={Fp}",
            device.DeviceName, result.HostKeyFingerprint);
    }

    private static List<string> ReadCommands(JsonElement args)
    {
        var list = new List<string>();
        if (args.TryGetProperty("commands", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in c.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
            }
        }
        return list;
    }

    private static bool Bool(JsonElement a, string k, bool def) =>
        a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : def;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
