# deploy/python — SSH runner (netmiko)

Nashira runs multi-vendor SSH through a **Python + netmiko runner** embedded in the
Docker image and invoked by the C# `SshCommandRunner` over a stdin/stdout JSON contract.
There is no pure-.NET equivalent for vendor-aware CLI handling; this mirrors flow-weaver's
approach (see `nashira_refactor.md` §6.2). The runner code was lifted from flow-weaver and
renamed; the two projects share the runner at the design/contract level only, not as a
live dependency.

## Files

- `nashira_ssh_runner.py`  — main runner. Two transports: netmiko (interactive PTY, default)
  and direct-exec (Paramiko `exec_command`, no PTY; auto-selected for `nokia_srl`). SHA256
  host-key pinning, password/key auth, enable mode, ANSI/control-char sanitation, optional
  TextFSM structured output.
- `nashira_ssh_parsers.py` — `device_type` normalization (`VENDOR_ALIASES`) + parser dispatch
  (local TextFSM bundle first, then upstream `ntc-templates`, then generic key-value).
- `nashira_ssh_clean.py`   — separator-line + generic key-value cleaning.
- `ntc_templates_extra/`  — local TextFSM template bundle (walked before upstream).
- `tests/`                — pytest suite for parsers and cleaning (`pytest deploy/python`).

## Invocation contract

The C# side spawns `python3 nashira_ssh_runner.py` and writes one JSON object to stdin.

### Input (stdin)

```json
{
  "host": "10.0.0.1",          "port": 22,
  "username": "netops",
  "password": "…",             "private_key": "-----BEGIN …",
  "key_passphrase": "…",       "enable_secret": "…",
  "device_type": "cisco_ios",  "commands": ["show version"],
  "stop_on_error": true,       "timeout_seconds": 30,
  "use_structured": false,     "preserve_ansi": false,
  "direct_exec": null,         "expected_fingerprint": "SHA256:…"
}
```

`host`, `username`, `commands` (non-empty) are required; at least one of `password` /
`private_key` must be present. `timeout_seconds` is clamped to `[5, 300]`.

### Output (stdout, exit 0)

```json
{
  "results": [
    { "command": "show version", "output": "…", "output_raw": "…",
      "parsed": null, "parser_used": null, "template_name": null,
      "elapsed_ms": 412, "ok": true, "error": null }
  ],
  "device_type": "cisco_ios", "device_type_resolved_from": null,
  "vendor": "cisco", "host_key_fingerprint": "SHA256:…",
  "device_type_fallback": null
}
```

### Exit codes (fatal errors: empty stdout, message on stderr)

| Code | Meaning |
|------|---------|
| 0 | Success (per-command failures are reported inside `results[].ok`) |
| 1 | Input parse error (invalid JSON, empty `commands`, missing required field) |
| 2 | Authentication failed |
| 3 | Connection timeout (TCP/SSH handshake) |
| 4 | Host-key mismatch (`expected_fingerprint` did not match) |
| 5 | Unexpected connect failure (unsupported device_type, I/O) |
| 99 | Unhandled runner exception (traceback on stderr) |

## C# side

- `Services/Ssh/SshCommandRunner` spawns the runner, writes the payload, reads stdout/stderr,
  enforces a `timeout_seconds + 30s` process deadline, and parses the result. It receives
  already-decrypted credentials; it never touches the database.
- `DeviceConnectHandler` (agent tool `device_connect`) resolves the device from inventory,
  decrypts the stored credential via `ISecretProtector`, and calls the runner.

Config keys (with defaults):

- `Python:Executable`   — `python3`
- `Python:SshRunnerPath` — `/usr/local/lib/nashira_python/nashira_ssh_runner.py`

## Runtime (hybrid .NET + Python image)

The image installs `python3 + pip` and:

```
pip install "netmiko>=4,<5" "paramiko>=3" "textfsm>=1.1" "ntc-templates>=4"
```

then copies the runner + helpers to `/usr/local/lib/` and sets `PYTHONPATH`. For the
**air-gapped** tier these dependencies are vendored (wheels) so the image builds and runs
without internet.
