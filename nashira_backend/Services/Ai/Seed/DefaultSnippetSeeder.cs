using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using IdempotencyTier = nashira_backend.Services.Workflow.Idempotency;
using SnippetEntity = nashira_backend.Data.Models.Snippet;

namespace nashira_backend.Services.Ai.Seed;

// One baseline snippet per registered handler, so the catalogue is usable on a fresh
// install instead of empty.
//
// The gap this closes is not cosmetic. A workflow node runs a *snippet*, never a type,
// so with an empty catalogue the agent has nothing legal to point a node at: it either
// stops at __start__ → __end__ or invents a placeholder in the style of the real
// literals. Both happened here — a hundred stored workflows whose nodes are only the
// two sentinels, and one carrying `__ping__`. Seeding gives the first turn something
// real to reference while `create_snippet` covers everything past the baseline.
//
// Idempotent by SLUG, not by type. Matching on type is what FlowWeaver does and it is
// wrong for this deployment: the snippets table already holds forty-five `ping` rows
// left by the e2e suite, so a type match would conclude ping was covered and skip it.
// A slug is permanent identity — a renamed baseline is still recognised, and an
// operator who deletes one gets it back on the next boot rather than a duplicate.
public static class DefaultSnippetSeeder
{
    private sealed record Template(
        string Slug,
        string Name,
        string Type,
        string Description,
        string TargetMode,
        int TimeoutSeconds,
        string? Code = null,
        string? ScriptLanguage = null,
        string? Idempotency = null,
        string? InputSchemaJson = null,
        string? OutputSchemaJson = null,
        // Both default to the value every baseline was seeded with before they
        // existed, so adding them changed nothing for the rows already here.
        bool NetworkEnabled = false,
        bool? ChangesState = null,
        string? LogicDiagramMermaid = null);

    // Deliberately small and deliberately generic: each one is the plainest useful
    // shape of its handler, configured entirely from the node's `config_overrides`.
    // Anything that needs a specific device, integration or credential is the
    // operator's to create — a baseline row that names a system this deployment does
    // not have would be seeded broken.
    // Exposed for the test that runs the REAL import scanner over the one baseline
    // carrying a body. Everything else here is declarative; that one is code, and a
    // refusal it introduces would otherwise surface on a live device rather than in CI.
    internal static string ParamikoCode =>
        Templates.First(t => t.Slug == "baseline-ssh-paramiko").Code!;

    private static readonly Template[] Templates =
    [
        new(
            Slug: "baseline-tcp-reachability",
            Name: "TCP reachability probe",
            Type: SnippetEntity.TypePing,
            Description: "Checks whether a host answers on a TCP port. Give it `host` (an IP, or an "
                + "inventory device name) and optionally `port` (default 22). Unreachable is a result "
                + "you branch on, not a step failure.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 15,
            Idempotency: IdempotencyTier.Idempotent,
            InputSchemaJson: """
                {"type":"object","required":["host"],"properties":{
                  "host":{"type":"string","description":"IP address, or the name of a device in inventory"},
                  "port":{"type":"integer","default":22}}}
                """,
            OutputSchemaJson: """
                {"type":"object","properties":{
                  "host":{"type":"string"},"port":{"type":"integer"},
                  "reachable":{"type":"boolean"},"latency_ms":{"type":"integer"}}}
                """),

        new(
            Slug: "baseline-tcp-reachability-per-device",
            Name: "TCP reachability probe (every target device)",
            Type: SnippetEntity.TypePing,
            Description: "The same probe, fanned out over every device the run targets. Use this "
                + "instead of the `once` variant when the workflow is about the fleet rather than "
                + "one named host; `{{ device.ip_address }}` resolves per device.",
            TargetMode: SnippetEntity.TargetPerDevice,
            TimeoutSeconds: 15,
            Idempotency: IdempotencyTier.Idempotent,
            InputSchemaJson: """
                {"type":"object","properties":{
                  "host":{"type":"string","default":"{{ device.ip_address }}"},
                  "port":{"type":"integer","default":22}}}
                """),

        new(
            Slug: "baseline-reshape-step-output",
            Name: "Reshape step output",
            Type: SnippetEntity.TypeTransform,
            Description: "Picks fields off the step input and emits them under new names. Supply "
                + "`mapping` (output name → path) and optionally `source` in config_overrides. Pure — "
                + "no I/O. Anything richer than a field pick belongs in a python_snippet.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 30,
            Idempotency: IdempotencyTier.Idempotent,
            InputSchemaJson: """
                {"type":"object","required":["mapping"],"properties":{
                  "mapping":{"type":"object","description":"output name -> dotted path over `source`. A non-string value is emitted literally."},
                  "source":{"description":"What to read paths against. Defaults to the whole step input."}}}
                """),

        new(
            Slug: "baseline-catalogued-api-call",
            Name: "Call a catalogued API operation",
            Type: SnippetEntity.TypeRestCall,
            Description: "Invokes one operation from a registered API spec through the REST executor, "
                + "which resolves the spec, its base URL, the linked integration's credentials and any "
                + "${secret:…} references. Give it `operation_id` plus optional `path_params`, "
                + "`query_params` and `body`. Declared requires_compensation because the handler cannot "
                + "know whether the operation reads or writes — lower it on a copy that wraps a GET.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 60,
            InputSchemaJson: """
                {"type":"object","required":["operation_id"],"properties":{
                  "operation_id":{"type":"string","description":"From discover_operations / operation_detail"},
                  "path_params":{"type":"object"},"query_params":{"type":"object"},"body":{}}}
                """),

        new(
            Slug: "baseline-integration-action",
            Name: "Run an integration action",
            Type: SnippetEntity.TypeIntegrationAction,
            Description: "Invokes a catalogued action on a registered integration. Give it "
                + "`integration` (the integration's name) and `action` (the action name or its "
                + "operation_id), plus any `path_params` / `query_params` / `body` the action takes. "
                + "Both are resolved by name, so a workflow using this survives being moved between "
                + "instances that each hold their own credentials.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 60,
            InputSchemaJson: """
                {"type":"object","required":["integration","action"],"properties":{
                  "integration":{"type":"string","description":"Name of a registered integration"},
                  "action":{"type":"string","description":"Action name, or its operation_id"},
                  "path_params":{"type":"object"},"query_params":{"type":"object"},"body":{}}}
                """),

        new(
            Slug: "baseline-device-cli-command",
            Name: "Run CLI commands on a device",
            Type: SnippetEntity.TypeSsh,
            Description: "Runs one or more CLI commands on an inventory device over netmiko, per "
                + "target device. Give it `commands` (a list). Each command is classified "
                + "independently — an unrecognised command counts as a mutation, and destructive ones "
                + "are blocked unless an admin has set Ssh:AllowDestructiveCommands. Prefer resolving "
                + "an intent through /api/vendor-commands/resolve over hard-coding per-vendor syntax.",
            TargetMode: SnippetEntity.TargetPerDevice,
            TimeoutSeconds: 120,
            InputSchemaJson: """
                {"type":"object","required":["commands"],"properties":{
                  "commands":{"type":"array","items":{"type":"string"},"description":"Commands to run, in order"}}}
                """),

        new(
            Slug: "baseline-mcp-tool-call",
            Name: "Call a tool on an MCP server",
            Type: SnippetEntity.TypeMcpCall,
            Description: "Invokes a tool on a registered MCP server. Give it `server` (the registered "
                + "server name), `tool` and `arguments`. non_reversible and it cannot be lowered: the "
                + "server is an arbitrary external program and nothing in the protocol says whether a "
                + "tool reads or writes, so no automatic compensation exists.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 60,
            InputSchemaJson: """
                {"type":"object","required":["server","tool"],"properties":{
                  "server":{"type":"string"},"tool":{"type":"string"},"arguments":{"type":"object"}}}
                """),

        new(
            Slug: "baseline-python-step",
            Name: "Python step",
            Type: SnippetEntity.TypePythonSnippet,
            Description: "Runs the author's Python against the step input in the sandbox. Replace the "
                + "body before using it for anything. Imports are checked against the tenant allowlist "
                + "BEFORE the interpreter starts, and only a module whose row is `ready` is importable; "
                + "dynamic import machinery is refused outright. This copy has no network access.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 60,
            ScriptLanguage: "python",
            Code: """
                # Baseline python step — replace this body.
                #
                # `input` is the resolved config_overrides for this node — that is the name the
                # sandbox harness binds, and the only one. Whatever you assign to `result`
                # IS the step's output, so a downstream node reads it as
                # {{ steps.<node_id>.output.<field> }} — no envelope, no extra level.
                # Printing a JSON object works too, for a script written against another
                # implementation of the contract; `result` wins if you do both. Anything else
                # you print is kept as the step's logs.
                import json

                result = {
                    "ok": True,
                    "received": input,
                }
                """,
            InputSchemaJson: """{"type":"object","description":"Whatever the node passes in config_overrides"}"""),

        // The one baseline that is seeded KNOWINGLY INERT, and the exception is
        // deliberate rather than an oversight.
        //
        // `paramiko` is a pip row that requires network, and PythonModuleSeeder
        // seeds neither: its rule is that the network opt-in must mean an admin
        // consciously approved a network module, not that shipping one snippet
        // pre-approved a socket for every other. So this row exists, is readable,
        // and refuses to run until an admin approves `paramiko` (pip, network) and
        // `io` (stdlib) at Admin → Python modules and turns on `network_enabled`.
        // The handler's refusal already names exactly those two fixes.
        //
        // Seeding it anyway is worth an inert row because the alternative is worse:
        // every deployment that needs an interactive SSH exchange — a password
        // change that prompts twice, a "[confirm y/n]" — writes its own expect loop
        // from scratch, and the two things they get wrong are the two things this
        // body gets right (the worker's own agent/~/.ssh keys are refused, and a
        // step carrying a secret keeps it out of the stored run output).
        new(
            Slug: "baseline-ssh-paramiko",
            Name: "SSH primitive (paramiko)",
            Type: SnippetEntity.TypePythonSnippet,
            Description: "Raw SSH via paramiko, for an interaction the `ssh` step cannot express. Two "
                + "modes on one connection: `mode='exec'` (default) runs each of `commands` on its own "
                + "channel and reports stdout/stderr/exit_status per command; `mode='shell'` opens ONE "
                + "interactive shell and drives it with `steps`, each {send, expect} — that is the mode "
                + "for a password change that prompts twice, a '[confirm y/n]' or a commit-confirm. "
                + "PREFER THE `ssh` STEP for ordinary send-and-read: it handles credentials, host keys "
                + "and vendor quirks. BEFORE THIS RUNS an admin must approve `paramiko` (pip, requires "
                + "network) and `io` (stdlib) under Admin → Python modules, and set `network_enabled` "
                + "on this snippet. Pass the target as `host` (use {{ device.ip }} on a per_device node) "
                + "and credentials as ${secret:credential:<name>:username} / :password / :private_key — "
                + "those resolve immediately before the interpreter starts, so nothing is stored in the "
                + "node. `ok: false` is a result to branch on; only connect/auth errors fail the step.",
            TargetMode: SnippetEntity.TargetPerDevice,
            TimeoutSeconds: 120,
            ScriptLanguage: "python",
            NetworkEnabled: false,
            // Conservative, and the node overrides it. A snippet that can answer a config
            // prompt is presumed to change something; a node whose commands are read-only
            // says so with `config_overrides.changes: false`, which wins over this. Null
            // would be the honest "the author has not said" — but for this type it also
            // means the step FAILS until some node declares it, and a baseline nobody can
            // run without first debugging it teaches nothing.
            ChangesState: true,
            LogicDiagramMermaid: """
                flowchart TD
                    in([config_overrides: host, credential, mode]) --> conn[SSHClient.connect - agent and ~/.ssh keys refused]
                    conn -->|auth or connect fails| fail([step FAILS])
                    conn --> mode{mode}
                    mode -->|exec| ex[exec_command per command]
                    ex --> exr([results: stdout, stderr, exit_status per command])
                    mode -->|shell| sh[invoke_shell, read the banner]
                    sh --> loop[per step: send, then read until expect]
                    loop -->|matched| loop
                    loop -->|no match and stop_on_error| stop[ok = false]
                    loop --> shr([transcript + matched per step])
                    stop --> shr
                    exr --> close[close the connection]
                    shr --> close
                    close --> out([ok, mode, host, results or transcript])
                """,
            InputSchemaJson: """
                {
                  "type": "object",
                  "required": ["host", "username"],
                  "properties": {
                    "mode":            { "type": "string", "enum": ["exec", "shell"], "default": "exec", "description": "'exec' runs each command on its own channel and reports exit_status per command — servers and Linux-like NOSes; many classic CLIs refuse exec entirely. 'shell' opens ONE interactive shell driven by `steps`, which is the only mode that can answer a prompt." },
                    "command":         { "type": "string", "description": "exec mode — shorthand for a single-entry `commands`." },
                    "commands":        { "type": "array", "items": { "type": "string" }, "description": "exec mode — one channel per entry, run in order." },
                    "steps":           { "type": "array", "description": "shell mode — the expect script, run in order.", "items": { "type": "object", "required": ["send"], "properties": {
                                           "send":            { "type": "string" },
                                           "expect":          { "type": "string", "description": "Regex to wait for after sending. Defaults to `shell_prompt`." },
                                           "terminator":      { "type": "string", "default": "\n" },
                                           "timeout_seconds": { "type": "integer" },
                                           "secret":          { "type": "boolean", "default": false, "description": "Replaces this step's `send` with *** in the output. Set it on anything carrying a password — the step output is a stored run artifact." },
                                           "redact_output":   { "type": "boolean", "default": false } } } },
                    "host":            { "type": "string", "description": "Target address. On a per_device node pass {{ device.ip }} — this step gets no device object of its own." },
                    "port":            { "type": "integer", "default": 22 },
                    "username":        { "type": "string", "description": "${secret:credential:<name|id>:username}" },
                    "password":        { "type": "string", "description": "${secret:credential:<name|id>:password}. Provide this or `private_key`." },
                    "private_key":     { "type": "string", "description": "${secret:credential:<name|id>:private_key} — PEM; ed25519 / ecdsa / rsa / dsa are all tried." },
                    "key_passphrase":  { "type": "string", "description": "${secret:credential:<name|id>:passphrase}" },
                    "shell_prompt":    { "type": "string", "default": "[>#$%]\\s*$", "description": "shell mode — what a step waits for when it declares no `expect` of its own." },
                    "connect_timeout_seconds": { "type": "integer", "default": 15 },
                    "command_timeout_seconds": { "type": "integer", "default": 30 },
                    "read_timeout_seconds":    { "type": "integer", "default": 15 },
                    "idle_seconds":    { "type": "number", "default": 1.0, "description": "shell mode — how long the channel must stay quiet before a step with no `expect` counts as finished." },
                    "stop_on_error":   { "type": "boolean", "default": true },
                    "strip_ansi":      { "type": "boolean", "default": true },
                    "host_key_policy": { "type": "string", "enum": ["auto_add", "reject"], "default": "auto_add", "description": "'auto_add' is trust-on-first-use: the host key is NOT verified. 'reject' verifies against the worker's known_hosts and fails on an unknown host." }
                  }
                }
                """,
            OutputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "ok":         { "type": "boolean", "description": "Every command exited 0 (exec), or every step matched its `expect` (shell)." },
                    "mode":       { "type": "string", "enum": ["exec", "shell"] },
                    "host":       { "type": "string" },
                    "port":       { "type": "integer" },
                    "results":    { "type": "array", "description": "exec mode.", "items": { "type": "object", "properties": {
                                      "command": { "type": "string" }, "stdout": { "type": "string" },
                                      "stderr": { "type": "string" }, "exit_status": { "type": "integer" },
                                      "ok": { "type": "boolean" } } } },
                    "failed":     { "type": "array", "items": { "type": "string" }, "description": "exec mode — the commands that exited non-zero." },
                    "steps":      { "type": "array", "description": "shell mode.", "items": { "type": "object", "properties": {
                                      "send": { "type": "string", "description": "*** when the step declared `secret`." },
                                      "expect": { "type": "string" }, "matched": { "type": "boolean" },
                                      "output": { "type": "string" } } } },
                    "transcript": { "type": "string", "description": "shell mode — the whole session, banner included." },
                    "unmatched":  { "type": "array", "items": { "type": "string" }, "description": "shell mode — the `expect` patterns that never showed up." }
                  }
                }
                """,
            Code: """
                # SSH primitive (paramiko) — one connection, two modes.
                #
                #   mode="exec"  (default)  each entry of `commands` runs on its own channel
                #                           via exec_command, so you get stdout, stderr and
                #                           exit_status per command. Servers and Linux-like
                #                           network OSes; many classic CLIs refuse exec.
                #   mode="shell"            ONE interactive shell driven by `steps`, each
                #                           {send, expect}. This is the mode for an exchange
                #                           exec cannot hold: a password change that asks
                #                           twice, a "[confirm y/n]", a commit-confirm.
                #
                # Deliberately NOT device-aware. This is the primitive the vendor-shaped
                # steps are built out of, not a replacement for them — when the built-in
                # `ssh` step covers the job, use that instead. Reach for this one when the
                # INTERACTION is the problem.
                #
                # Contract: `input` is the resolved config_overrides (the only name the
                # harness binds), and whatever is assigned to `result` IS the step output.
                # There is no device object here — pass {{ device.ip }} as `host`.

                import io
                import re
                import time

                import paramiko

                # ANSI/CSI/OSC and the C0 control bytes a live TTY sprays into a shell
                # channel. Left in, they turn an `expect` regex into a coin flip.
                #
                # Kept as a PATTERN STRING, not a compiled object, and every regex below
                # goes through the module-level re.* functions for the same reason: the
                # import scanner refuses a snippet that calls the builtin whose name is
                # com-pile, and it matches on TEXT, so re's own version of it never
                # reaches the interpreter — and neither may a comment that spells the call
                # out. re caches patterns internally, so this costs nothing; do not
                # "optimise" it back.
                _NOISE = (
                    r"\x1b\[[0-9;?]*[ -/]*[@-~]"
                    r"|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)"
                    r"|\x1b[@-Z\\-_]"
                    r"|[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")


                def _clean(text, strip):
                    return re.sub(_NOISE, "", text) if strip else text


                def _load_key(pem, passphrase):
                    # paramiko has no "work out the type" loader, so try each class and keep
                    # the first that parses. The order is cheapest-first, not a preference.
                    last = None
                    for cls in (paramiko.Ed25519Key, paramiko.ECDSAKey, paramiko.RSAKey, paramiko.DSSKey):
                        try:
                            return cls.from_private_key(io.StringIO(pem), password=passphrase or None)
                        except Exception as err:
                            last = err
                    raise ValueError("private_key did not parse as ed25519, ecdsa, rsa or dsa: %s" % last)


                def _config(inp):
                    host = inp.get("host")
                    if not host:
                        raise ValueError("no host: pass `host` — on a per_device node, {{ device.ip }}")

                    username = inp.get("username")
                    password = inp.get("password")
                    private_key = inp.get("private_key")
                    if not username:
                        raise ValueError(
                            "no username: pass `username`, e.g. "
                            "${secret:credential:<name>:username} — the reference is resolved "
                            "before this script runs, so no plaintext is stored on the node")
                    if not password and not private_key:
                        raise ValueError("no credential: pass `password` or `private_key`")

                    mode = str(inp.get("mode") or "exec").strip().lower()
                    if mode not in ("exec", "shell"):
                        raise ValueError("mode must be 'exec' or 'shell', not %r" % mode)

                    commands = inp.get("commands")
                    if not commands and inp.get("command"):
                        commands = [inp["command"]]
                    steps = inp.get("steps") or []

                    if mode == "exec" and not commands:
                        raise ValueError("mode='exec' needs `command` or `commands`")
                    if mode == "shell" and not steps:
                        raise ValueError("mode='shell' needs `steps`, each {send, expect}")

                    return {
                        "host": host,
                        "port": int(inp.get("port") or 22),
                        "username": username,
                        "password": password,
                        "private_key": private_key,
                        "key_passphrase": inp.get("key_passphrase"),
                        "mode": mode,
                        "commands": list(commands or []),
                        "steps": list(steps),
                        "connect_timeout": int(inp.get("connect_timeout_seconds") or 15),
                        "command_timeout": int(inp.get("command_timeout_seconds") or 30),
                        "read_timeout": int(inp.get("read_timeout_seconds") or 15),
                        "idle": float(inp.get("idle_seconds") or 1.0),
                        "prompt": inp.get("shell_prompt") or r"[>#$%]\s*$",
                        "stop_on_error": bool(inp.get("stop_on_error", True)),
                        "strip_ansi": bool(inp.get("strip_ansi", True)),
                        "host_key_policy": str(inp.get("host_key_policy") or "auto_add").lower(),
                    }


                def _connect(cfg):
                    client = paramiko.SSHClient()
                    if cfg["host_key_policy"] == "reject":
                        client.load_system_host_keys()
                        client.set_missing_host_key_policy(paramiko.RejectPolicy())
                    else:
                        # Trust on first use, said out loud rather than left to look like
                        # pinning: under the default policy this primitive does NOT verify
                        # the host key.
                        client.set_missing_host_key_policy(paramiko.AutoAddPolicy())

                    kwargs = {
                        "hostname": cfg["host"],
                        "port": cfg["port"],
                        "username": cfg["username"],
                        "timeout": cfg["connect_timeout"],
                        "banner_timeout": cfg["connect_timeout"],
                        "auth_timeout": cfg["connect_timeout"],
                        # The WORKER's ssh-agent and ~/.ssh keys are not this step's
                        # credentials. Left on, paramiko tries them silently and a workflow
                        # reaches a device nobody ever gave it a credential for.
                        "allow_agent": False,
                        "look_for_keys": False,
                    }
                    if cfg["private_key"]:
                        kwargs["pkey"] = _load_key(cfg["private_key"], cfg["key_passphrase"])
                    else:
                        kwargs["password"] = cfg["password"]

                    client.connect(**kwargs)
                    return client


                def _run_commands(client, cfg):
                    results = []
                    ok = True
                    for command in cfg["commands"]:
                        stdin, stdout, stderr = client.exec_command(command, timeout=cfg["command_timeout"])
                        stdin.close()
                        out = stdout.read().decode("utf-8", "replace")
                        err = stderr.read().decode("utf-8", "replace")
                        status = stdout.channel.recv_exit_status()
                        results.append({
                            "command": command,
                            "stdout": _clean(out, cfg["strip_ansi"]),
                            "stderr": _clean(err, cfg["strip_ansi"]),
                            "exit_status": status,
                            "ok": status == 0,
                        })
                        if status != 0:
                            ok = False
                            if cfg["stop_on_error"]:
                                break
                    return {
                        "mode": "exec",
                        "ok": ok,
                        "results": results,
                        "failed": [r["command"] for r in results if not r["ok"]],
                    }


                def _read(chan, pattern, timeout, idle, strip):
                    # Returns (text, matched). WITH a pattern, matched means it showed up
                    # before the deadline. WITHOUT one the only stop condition is silence: a
                    # device sitting at its prompt with nothing left to say looks exactly
                    # like one that never started, so `idle` is what decides it is done.
                    deadline = time.monotonic() + timeout
                    buf = ""
                    last = time.monotonic()
                    while time.monotonic() < deadline:
                        chan.settimeout(0.5)
                        try:
                            chunk = chan.recv(65535)
                        except TimeoutError:
                            # socket.timeout IS TimeoutError from Python 3.10 on, so this
                            # catches paramiko's read timeout without importing socket.
                            chunk = b""
                        except paramiko.SSHException:
                            break
                        if chunk:
                            buf += chunk.decode("utf-8", "replace")
                            last = time.monotonic()
                            if pattern and re.search(pattern, _clean(buf, strip)):
                                return _clean(buf, strip), True
                            continue
                        if chan.closed or chan.eof_received:
                            break
                        if not pattern and buf and (time.monotonic() - last) >= idle:
                            return _clean(buf, strip), True
                    return _clean(buf, strip), (not pattern) and bool(buf)


                def _run_shell(client, cfg):
                    # Wide and tall on purpose: a narrow pty wraps lines mid-token and the
                    # `expect` never matches what the operator typed into the node.
                    chan = client.invoke_shell(width=511, height=1000)
                    try:
                        banner, _ = _read(
                            chan, cfg["prompt"], cfg["read_timeout"], cfg["idle"], cfg["strip_ansi"])
                        transcript = banner
                        performed = []
                        ok = True
                        for step in cfg["steps"]:
                            send = step.get("send", "")
                            expect = step.get("expect") or cfg["prompt"]
                            timeout = int(step.get("timeout_seconds") or cfg["read_timeout"])
                            terminator = step.get("terminator")
                            if terminator is None:
                                terminator = "\n"
                            chan.send((send + terminator).encode("utf-8"))
                            out, matched = _read(chan, expect, timeout, cfg["idle"], cfg["strip_ansi"])
                            transcript += out
                            secret = bool(step.get("secret"))
                            performed.append({
                                # A step marked `secret` keeps its payload out of the step
                                # output. The output is a STORED run artifact — a new
                                # password sent to a device must not come back as something
                                # anyone with read access can page through.
                                "send": "***" if secret else send,
                                "expect": expect,
                                "matched": matched,
                                "output": "***" if secret and step.get("redact_output") else out,
                            })
                            if not matched:
                                ok = False
                                if cfg["stop_on_error"]:
                                    break
                        return {
                            "mode": "shell",
                            "ok": ok,
                            "steps": performed,
                            "transcript": transcript,
                            "unmatched": [s["expect"] for s in performed if not s["matched"]],
                        }
                    finally:
                        chan.close()


                _cfg = _config(input)
                _client = _connect(_cfg)
                try:
                    result = _run_commands(_client, _cfg) if _cfg["mode"] == "exec" else _run_shell(_client, _cfg)
                finally:
                    _client.close()

                result["host"] = _cfg["host"]
                result["port"] = _cfg["port"]
                """),

        new(
            Slug: "baseline-git-read-file",
            Name: "Read a file from a repository",
            Type: SnippetEntity.TypeGit,
            Description: "Reads one file out of a registered repository's working copy. Give it "
                + "`repository_id` (from Git → repositories) and `path`; `ref` picks a branch or sha and "
                + "defaults to the checked-out one. Returns `content` as text, or base64 with "
                + "`is_binary: true`. Idempotent — it is the read half of the git node.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 60,
            Idempotency: IdempotencyTier.Idempotent,
            InputSchemaJson: """
                {"type":"object","required":["repository_id","path"],"properties":{
                  "operation":{"type":"string","const":"read_file"},
                  "repository_id":{"type":"string","description":"Registered repository uuid"},
                  "path":{"type":"string","description":"Repo-relative path"},
                  "ref":{"type":"string","description":"Branch, tag or sha. Defaults to the checked-out ref."}}}
                """,
            OutputSchemaJson: """
                {"type":"object","properties":{
                  "path":{"type":"string"},"ref":{"type":"string"},"content":{"type":"string"},
                  "size":{"type":"integer"},"is_binary":{"type":"boolean"}}}
                """),

        new(
            Slug: "baseline-git-write-file",
            Name: "Commit a file to a repository",
            Type: SnippetEntity.TypeGit,
            Description: "Writes a file into a registered repository and commits it; set `push: true` to "
                + "send it to the remote in the same step. This is how a run puts a generated report or "
                + "a captured config somewhere a person can review it. `content` REPLACES the whole file "
                + "— there is no patch mode, so a step meaning to append must read first. "
                + "requires_compensation: a push is undone by a revert commit, which the failure edge "
                + "has to wire.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 120,
            Idempotency: IdempotencyTier.RequiresCompensation,
            InputSchemaJson: """
                {"type":"object","required":["repository_id","path","commit_message"],"properties":{
                  "operation":{"type":"string","const":"write_file"},
                  "repository_id":{"type":"string","description":"Registered repository uuid"},
                  "path":{"type":"string"},
                  "content":{"type":"string","description":"The complete new file body"},
                  "commit_message":{"type":"string"},
                  "branch":{"type":"string"},
                  "push":{"type":"boolean","default":false},
                  "author_name":{"type":"string"},"author_email":{"type":"string"}}}
                """,
            OutputSchemaJson: """
                {"type":"object","properties":{
                  "ok":{"type":"boolean"},"operation":{"type":"string"},"commit_sha":{"type":"string"},
                  "branch":{"type":"string"},"message":{"type":"string"}}}
                """),

        new(
            Slug: "baseline-git-pull",
            Name: "Pull a repository",
            Type: SnippetEntity.TypeGit,
            Description: "Fetches and fast-forwards the working copy so later steps read current files. "
                + "Give it `repository_id` and optionally `branch`. Clones on first use. Idempotent: "
                + "pulling twice leaves the same tree.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 120,
            Idempotency: IdempotencyTier.Idempotent,
            InputSchemaJson: """
                {"type":"object","required":["repository_id"],"properties":{
                  "operation":{"type":"string","const":"pull"},
                  "repository_id":{"type":"string"},
                  "branch":{"type":"string"}}}
                """),

        // FlowWeaver-parity types. netconf and snmp_v3 are registered as stubs and
        // deliberately get NO baseline: a seeded snippet that can only fail would be
        // a booby trap in the catalogue, while an unseeded reserved type is merely
        // absent until its client library lands.
        new(
            Slug: "baseline-report",
            Name: "Generate a report",
            Type: SnippetEntity.TypeReport,
            Description: "Renders `content` (markdown) into a stored report — markdown, html or pdf — "
                + "linked to this run in /reports. The output's `base64` is the file itself, ready to "
                + "attach on a downstream email_send step.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 60,
            Idempotency: IdempotencyTier.Idempotent,
            InputSchemaJson: """
                {"type":"object","required":["title","content"],"properties":{
                  "title":{"type":"string"},
                  "content":{"type":"string","description":"Markdown body; {{ steps.x.output.y }} references resolve first"},
                  "format":{"type":"string","enum":["markdown","html","pdf"],"default":"markdown"},
                  "description":{"type":"string"},
                  "file_name":{"type":"string"},
                  "retain_days":{"type":"integer","minimum":1}}}
                """,
            OutputSchemaJson: """
                {"type":"object","properties":{
                  "report_artifact_id":{"type":"string"},"title":{"type":"string"},
                  "file_name":{"type":"string"},"content_type":{"type":"string"},
                  "format":{"type":"string"},"size_bytes":{"type":"integer"},
                  "download_url":{"type":"string"},"base64":{"type":"string"}}}
                """),

        new(
            Slug: "baseline-email-send",
            Name: "Send an email",
            Type: SnippetEntity.TypeEmailSend,
            Description: "Sends mail through a named email channel (`channel` = slug or id from "
                + "/admin/email) or, when omitted, the deployment's default relay. Attachments take "
                + "base64 content — `{{ steps.report.output.base64 }}` pairs with the report snippet. "
                + "Non-reversible: a delivered email cannot be recalled.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 60,
            Idempotency: IdempotencyTier.NonReversible,
            InputSchemaJson: """
                {"type":"object","required":["subject"],"properties":{
                  "channel":{"type":"string","description":"Email channel slug or id; omit for the default relay"},
                  "to":{"description":"String, csv string or array of addresses"},
                  "cc":{},"bcc":{},
                  "subject":{"type":"string"},
                  "body":{"type":"string"},"html":{"type":"string"},
                  "reply_to":{"type":"string"},
                  "attachments":{"type":"array","items":{"type":"object",
                    "required":["file_name","content_base64"],
                    "properties":{"file_name":{"type":"string"},
                      "content_base64":{"type":"string"},
                      "content_type":{"type":"string"}}}}}}
                """,
            OutputSchemaJson: """
                {"type":"object","properties":{
                  "ok":{"type":"boolean"},"channel_name":{"type":"string"},
                  "recipients":{"type":"integer"},"attachments":{"type":"integer"}}}
                """),

        new(
            Slug: "baseline-email-mailbox",
            Name: "Manage a mailbox",
            Type: SnippetEntity.TypeEmailMailbox,
            Description: "Operates on the inbound (IMAP) side of an email channel: list or read "
                + "messages, mark them read/unread, move, archive or delete them. `channel` = slug or "
                + "id from /admin/email with an IMAP host configured; omitted, the oldest such channel "
                + "is used. A list step's `uids` output feeds the mark/move/archive/delete actions.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 90,
            Idempotency: IdempotencyTier.RequiresCompensation,
            InputSchemaJson: """
                {"type":"object","required":["action"],"properties":{
                  "action":{"type":"string","enum":["list","read","mark","move","archive","delete","folders"]},
                  "channel":{"type":"string","description":"Email channel slug or id with IMAP configured"},
                  "folder":{"type":"string","description":"Mailbox folder (default INBOX)"},
                  "unread_only":{"type":"boolean"},
                  "from_contains":{"type":"string"},"subject_contains":{"type":"string"},
                  "text_contains":{"type":"string"},
                  "since_days":{"type":"integer","minimum":1},
                  "limit":{"type":"integer","minimum":1,"maximum":100},
                  "uid":{"type":"integer","description":"read (or single-message mark/move/archive/delete)"},
                  "uids":{"type":"array","items":{"type":"integer"}},
                  "seen":{"type":"boolean","description":"mark: true = read, false = unread"},
                  "target_folder":{"type":"string","description":"move: destination folder"},
                  "permanent":{"type":"boolean","description":"delete: expunge instead of Trash; unrecoverable"}}}
                """,
            OutputSchemaJson: """
                {"type":"object","properties":{
                  "ok":{"type":"boolean"},"channel_name":{"type":"string"},
                  "count":{"type":"integer"},"uids":{"type":"array","items":{"type":"integer"}},
                  "messages":{"type":"array"},"folders":{"type":"array"},
                  "marked":{"type":"integer"},"moved":{"type":"integer"},
                  "archived":{"type":"integer"},"deleted":{"type":"integer"},
                  "requested":{"type":"integer"},"outcome":{"type":"string"},
                  "archive_folder":{"type":"string"}}}
                """),

        new(
            Slug: "baseline-slack-message",
            Name: "Message a channel",
            Type: SnippetEntity.TypeSlackMessage,
            Description: "Posts `text` to a messaging channel (`channel` = slug or id from "
                + "/admin/notifications — Slack, Teams or generic webhook). The delivery is recorded "
                + "against the run. Non-reversible: a sent message has no undo.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 30,
            Idempotency: IdempotencyTier.NonReversible,
            InputSchemaJson: """
                {"type":"object","required":["channel","text"],"properties":{
                  "channel":{"type":"string","description":"Messaging channel slug or id"},
                  "text":{"type":"string"}}}
                """,
            OutputSchemaJson: """
                {"type":"object","properties":{
                  "ok":{"type":"boolean"},"channel_name":{"type":"string"},"kind":{"type":"string"},
                  "status_code":{"type":"integer"},"attempts":{"type":"integer"},
                  "elapsed_ms":{"type":"integer"}}}
                """),

        new(
            Slug: "baseline-ansible-playbook",
            Name: "Run an Ansible playbook",
            Type: SnippetEntity.TypeAnsiblePlaybook,
            Description: "Runs the snippet's YAML playbook against one target: `device` (inventory "
                + "name or IP, environment-gated like ssh) or a literal `host`. The resolved input "
                + "travels as --extra-vars. The baseline body only gathers facts — replace it. "
                + "Linux worker only; needs ansible-playbook installed.",
            TargetMode: SnippetEntity.TargetOnce,
            TimeoutSeconds: 600,
            Idempotency: IdempotencyTier.NonReversible,
            Code: """
                ---
                - name: Baseline connectivity check
                  hosts: targets
                  gather_facts: true
                  tasks:
                    - name: Report reachability
                      ansible.builtin.ping:
                """,
            InputSchemaJson: """
                {"type":"object","properties":{
                  "device":{"type":"string","description":"Inventory device name or IP"},
                  "host":{"type":"string","description":"Literal target outside inventory"},
                  "playbook":{"type":"string","description":"Inline YAML override; normally the snippet's code"}}}
                """,
            OutputSchemaJson: """
                {"type":"object","properties":{
                  "exit_code":{"type":"integer"},"host":{"type":"string"},
                  "recap":{"type":["object","null"]}}}
                """),
    ];

    public static async Task SeedAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Only seed handlers this build can actually execute. Seeding a row whose type
        // has no handler produces a snippet that validates at write time and fails with
        // `unknown_handler` on the device — the exact class of late failure the
        // reference checker exists to prevent.
        var known = scope.ServiceProvider
            .GetRequiredService<Services.Worker.ISnippetHandlerRegistry>()
            .KnownTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var wanted = Templates.Where(t => known.Contains(t.Type)).ToList();
        var slugs = wanted.Select(t => t.Slug).ToList();

        var present = await db.Snippets
            .Where(s => slugs.Contains(s.Slug))
            .Select(s => s.Slug)
            .ToListAsync(ct);
        var have = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);

        var takenNames = new HashSet<string>(
            await db.Snippets.Where(s => s.IsActive).Select(s => s.Name).ToListAsync(ct),
            StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var added = 0;
        foreach (var t in wanted)
        {
            if (have.Contains(t.Slug)) continue;

            // The name column is unique among active rows. A deployment that already
            // has an operator's snippet under this name keeps it — the baseline takes
            // a suffixed name rather than failing the whole seed on one collision.
            var name = takenNames.Contains(t.Name) ? UniqueName(t.Name, takenNames) : t.Name;
            takenNames.Add(name);

            db.Snippets.Add(new SnippetEntity
            {
                SnippetId = Guid.NewGuid(),
                Name = name,
                Slug = t.Slug,
                Type = t.Type,
                Description = t.Description,
                Code = t.Code,
                ScriptLanguage = t.ScriptLanguage,
                InputSchemaJson = t.InputSchemaJson,
                OutputSchemaJson = t.OutputSchemaJson,
                TargetMode = t.TargetMode,
                TimeoutSeconds = t.TimeoutSeconds,
                Idempotency = t.Idempotency,
                ChangesState = t.ChangesState,
                LogicDiagramMermaid = t.LogicDiagramMermaid,
                // Nothing has run these on this instance, and `verified` marks
                // provenance for the reviewer looking at a promotion. A seeded row is
                // not a reviewed one.
                Verified = false,
                NetworkEnabled = t.NetworkEnabled,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            added++;
        }

        var repaired = await RepairBrokenPythonBaselineAsync(db, logger, now, ct);

        if (added == 0 && repaired == 0)
        {
            logger.LogInformation("snippets.seed.noop present={Present}", have.Count);
            return;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "snippets.seed.added count={Count} skipped={Skipped} repaired={Repaired}",
            added, have.Count, repaired);
    }

    // The python baseline shipped for a while with a body referencing `payload`, a name
    // the sandbox harness never binds — it binds `input`. Every copy of it raised
    // NameError on the first run, which reads as "the python step is broken" rather than
    // "the example is wrong", and seeding is idempotent by slug so a redeploy alone would
    // never have replaced it.
    //
    // Narrow on purpose: only a row still carrying the exact broken line is rewritten, so
    // an operator who edited the baseline into something real keeps it.
    private static async Task<int> RepairBrokenPythonBaselineAsync(
        AppDbContext db, ILogger logger, DateTime now, CancellationToken ct)
    {
        const string brokenMarker = "\"received\": payload";
        var template = Templates.First(t => t.Slug == "baseline-python-step");

        var rows = await db.Snippets
            .Where(s => s.Slug == "baseline-python-step" && s.Code != null && s.Code.Contains(brokenMarker))
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            row.Code = template.Code;
            row.Description = template.Description;
            row.UpdatedAt = now;
            logger.LogInformation("snippets.seed.repaired snippet_id={SnippetId}", row.SnippetId);
        }

        return rows.Count;
    }

    private static string UniqueName(string baseName, IReadOnlySet<string> taken)
    {
        for (var n = 2; n < 100; n++)
        {
            var candidate = $"{baseName} ({n})";
            if (!taken.Contains(candidate)) return candidate;
        }
        return $"{baseName} ({Guid.NewGuid():N}[..6])";
    }
}
