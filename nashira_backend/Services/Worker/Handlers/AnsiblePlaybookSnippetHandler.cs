using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Security;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Worker.Handlers;

// Executes an Ansible playbook — FlowWeaver's `ansible_playbook`, with Nashira's
// targeting. The playbook YAML lives on the snippet row (`code`), like
// python_snippet scripts.
//
// Targets, per workflow.v1 snippets/SPEC.md:
//   hosts   (C) — an array of inventory device names (or IPs), or the string `all`
//   device  (A) — one inventory device, name or IP, exactly like the ssh snippet
//   host    (A) — one literal address outside inventory
//   targets (A) — same as `hosts`
// Inventory devices get the run's environment gate (the same allow trio the ssh
// snippet enforces) and their credential's password; a literal `host` gets neither.
// `hosts: all` means the run's target devices, which this handler is not handed —
// it fails with `not_supported` and says so rather than guessing.
//
// The inventory written for the run lists every host; one shared password travels
// over env (ANSIBLE_SSH_PASS, never argv), and only when hosts need different
// passwords are they written per host into the inventory file — owner-only, in a
// temp dir deleted when the run ends. The whole resolved input travels as
// --extra-vars so playbooks read runtime parameters via `{{ key }}` lookups.
// Key-based auth is not wired — matches the FlowWeaver handler's scope.
//
// Linux only: Windows returns a clear error rather than crashing on a missing
// `ansible-playbook` binary.
public sealed class AnsiblePlaybookSnippetHandler : ISnippetHandler
{
    private readonly AppDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly ILogger<AnsiblePlaybookSnippetHandler> _logger;

    /// <summary>
    /// What the play receives as <c>--extra-vars</c>: the whole resolved payload, so a
    /// playbook reads any runtime parameter the node supplied through an ordinary
    /// <c>{{ key }}</c> lookup.
    /// </summary>
    /// <remarks>
    /// Extracted from the run path so it can be observed without a Linux host and an
    /// `ansible-playbook` binary. It is the only part of this handler the interchange
    /// contract can assert: the payload the play is handed is what a portable workflow
    /// depends on, and it is invisible in the step's output — a run that passed the wrong
    /// variables and a run that passed the right ones look identical afterwards.
    ///
    /// The oracle does exactly this too (`AnsibleHandler.cs`, `input.GetRawText()`), which
    /// is why the contract describes the wide behaviour rather than the narrow one an
    /// earlier draft asserted.
    /// </remarks>
    public static string ExtraVarsJson(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object ? input.GetRawText() : "{}";

    public AnsiblePlaybookSnippetHandler(
        AppDbContext db, ISecretProtector protector, ILogger<AnsiblePlaybookSnippetHandler> logger)
    {
        _db = db;
        _protector = protector;
        _logger = logger;
    }

    public string Type => Data.Models.Snippet.TypeAnsiblePlaybook;

    // Playbooks mutate device state with no automatic compensation.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.NonReversible;

    // One line of the inventory the run is given.
    private sealed record Target(string Host, string? Username, string? Password);

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return SnippetResult.Fail(
                "ansible_playbook only runs on Linux (container deployment)", "unsupported_platform");

        var input = request.Input;

        // Playbook source: the snippet body first, an inline override second.
        var playbook = request.Code;
        if (string.IsNullOrWhiteSpace(playbook)) playbook = Str(input, "playbook");
        if (string.IsNullOrWhiteSpace(playbook))
            return SnippetResult.Fail(
                "the snippet has no playbook — put the YAML in the snippet's `code`", "bad_input");

        var timeoutSec = Math.Clamp(request.TimeoutSeconds <= 0 ? 600 : request.TimeoutSeconds, 10, 3600);

        var (deviceRefs, all) = HostRefs(input);
        if (all)
            return SnippetResult.Fail(
                "`hosts: all` (the run's target devices) is not supported by this build — list the device names",
                "not_supported");

        // Resolve every inventory device: environment gate, then credential.
        var targets = new List<Target>();
        foreach (var deviceRef in deviceRefs)
        {
            var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(
                d => d.IsActive && (d.DeviceName == deviceRef || d.IpAddress == deviceRef), ct);
            if (device is null)
                return SnippetResult.Fail($"device '{deviceRef}' is not in inventory", "not_found");

            if (DeviceEnvironmentPolicy.Refusal(device, request.Environment) is { } refusal)
            {
                _logger.LogWarning(
                    "workflow.ansible.environment_refused device={Device} environment={Environment}",
                    device.DeviceName, request.Environment);
                return SnippetResult.Fail(refusal, "environment_denied");
            }

            string? username = null, password = null;
            if (device.CredentialId is { } credId)
            {
                var credential = await _db.Credentials.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.IsActive && c.CredentialId == credId, ct);
                if (credential is not null
                    && credential.AuthMethod == Credential.AuthMethodPassword
                    && !string.IsNullOrWhiteSpace(credential.Username))
                {
                    username = credential.Username;
                    try
                    {
                        password = _protector.Decrypt(credential.EncryptedPassword);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "workflow.ansible.decrypt_failed credential={Credential}",
                            credential.Name);
                    }
                }
            }
            targets.Add(new Target(device.IpAddress, username, password));
        }

        // `host` is a literal for machines outside inventory.
        var literalHost = Str(input, "host");
        if (!string.IsNullOrWhiteSpace(literalHost))
            targets.Add(new Target(literalHost!.Trim(), null, null));

        if (targets.Count == 0)
            return SnippetResult.Fail(
                "cannot resolve a target — give `hosts` (inventory names), `device` (inventory name or IP) or `host`",
                "bad_input");

        var extraVarsJson = ExtraVarsJson(input);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"na-ansible-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var playbookFile = Path.Combine(tmpDir, "playbook.yml");
        var inventoryFile = Path.Combine(tmpDir, "inventory.ini");

        try
        {
            await File.WriteAllTextAsync(playbookFile, playbook, ct);

            // One password shared by every host travels as an env var rather than on
            // the CLI, so it never appears in the process listing. Only when hosts
            // need different passwords do they go per host into the inventory —
            // owner-only, and gone with the temp dir.
            var passwords = targets.Select(t => t.Password).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            var sharedPassword = passwords.Count == 1 ? passwords[0] : null;

            var inventory = new StringBuilder();
            inventory.AppendLine("[targets]");
            foreach (var t in targets)
            {
                inventory.Append(t.Host);
                if (!string.IsNullOrWhiteSpace(t.Username))
                    inventory.Append($" ansible_user={t.Username}");
                if (sharedPassword is null && !string.IsNullOrEmpty(t.Password))
                    inventory.Append($" ansible_password={Quote(t.Password!)}");
                inventory.AppendLine();
            }
            await File.WriteAllTextAsync(inventoryFile, inventory.ToString(), ct);
            if (sharedPassword is null && passwords.Count > 1)
                File.SetUnixFileMode(inventoryFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var psi = new ProcessStartInfo
            {
                FileName = "ansible-playbook",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tmpDir,
            };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(inventoryFile);
            psi.ArgumentList.Add(playbookFile);
            psi.ArgumentList.Add("--extra-vars");
            psi.ArgumentList.Add(extraVarsJson);
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("targets");

            psi.EnvironmentVariables["ANSIBLE_HOST_KEY_CHECKING"] = "False";
            psi.EnvironmentVariables["ANSIBLE_STDOUT_CALLBACK"] = "default";
            if (!string.IsNullOrWhiteSpace(sharedPassword))
                psi.EnvironmentVariables["ANSIBLE_SSH_PASS"] = sharedPassword;

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return SnippetResult.Fail($"ansible-playbook timed out after {timeoutSec}s", "timeout",
                    retryable: true);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var success = proc.ExitCode == 0;
            var recaps = targets.ToDictionary(t => t.Host, t => ParsePlayRecap(stdout, t.Host));
            var firstHost = targets[0].Host;

            return new SnippetResult
            {
                Success = success,
                // Ansible reports it: every play recap carries a `changed=` count, and the
                // output below already aggregates them. A playbook that ran and changed
                // nothing is the case this whole signal exists to tell apart, and here it is
                // measured rather than assumed from the handler's tier.
                Change = recaps.Values.Any(r => r is { Changed: > 0 })
                    ? StepChange.Changed
                    : StepChange.Unchanged,
                Output = JsonSerializer.SerializeToElement(new
                {
                    // The contract's portable quartet.
                    ok = success,
                    changed = recaps.Values.Any(r => r is { Changed: > 0 }),
                    stdout,
                    stats = recaps.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                    exit_code = proc.ExitCode,
                    host = firstHost,
                    hosts = targets.Select(t => t.Host).ToList(),
                    // The PLAY RECAP line for the first target, so downstream steps can
                    // condition on ok/changed/failed without parsing full stdout.
                    recap = recaps[firstHost] is { } r ? new { raw = r.Raw } : null,
                }),
                Logs = $"exit={proc.ExitCode}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}",
                Error = success ? string.Empty : $"exit code {proc.ExitCode}: {stderr}".Trim(),
                ErrorCode = success ? null : "playbook_failed",
            };
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogError(ex, "workflow.ansible.binary_missing");
            return SnippetResult.Fail(
                "ansible-playbook is not installed in the worker image", "binary_missing");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* cleanup best effort */ }
        }
    }

    // The inventory references the payload names, and whether it asked for `all`.
    // `hosts` (canonical) and `targets` (alias) take an array or a lone string;
    // `device` (alias) one string. Internal so the rule is testable without a
    // Linux worker.
    internal static (List<string> Refs, bool All) HostRefs(JsonElement input)
    {
        var refs = new List<string>();
        var all = false;
        if (input.ValueKind != JsonValueKind.Object) return (refs, all);

        foreach (var key in new[] { "hosts", "targets" })
        {
            if (!input.TryGetProperty(key, out var el)) continue;
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString()?.Trim();
                if (string.Equals(s, "all", StringComparison.OrdinalIgnoreCase)) all = true;
                else if (!string.IsNullOrWhiteSpace(s)) refs.Add(s!);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var s = item.GetString()?.Trim();
                    if (string.Equals(s, "all", StringComparison.OrdinalIgnoreCase)) all = true;
                    else if (!string.IsNullOrWhiteSpace(s)) refs.Add(s!);
                }
            }
        }

        var device = Str(input, "device")?.Trim();
        if (!string.IsNullOrWhiteSpace(device)) refs.Add(device!);

        return (refs.Distinct(StringComparer.Ordinal).ToList(), all);
    }

    private sealed record PlayRecap(string Raw, int Ok, int Changed, int Unreachable, int Failed);

    private static PlayRecap? ParsePlayRecap(string stdout, string host)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(host, StringComparison.OrdinalIgnoreCase)) continue;
            if (!trimmed.Contains("ok=", StringComparison.Ordinal)) continue;
            return new PlayRecap(trimmed, Count(trimmed, "ok="), Count(trimmed, "changed="),
                Count(trimmed, "unreachable="), Count(trimmed, "failed="));
        }
        return null;
    }

    private static int Count(string recap, string key)
    {
        var i = recap.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return 0;
        var start = i + key.Length;
        var end = start;
        while (end < recap.Length && char.IsDigit(recap[end])) end++;
        return int.TryParse(recap[start..end], out var n) ? n : 0;
    }

    // INI inventory values: single quotes, with embedded quotes escaped.
    private static string Quote(string s) => "'" + s.Replace("'", "'\"'\"'") + "'";

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
