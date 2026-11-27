# Python execution configuration

`python_snippet` is disabled by default (`sandbox_not_configured`). The module
allowlist and restricted Python builtins are not an OS sandbox.

These settings are deployment configuration, read when the execution scope is
created. They are deliberately absent from the writable admin-settings catalog;
`PUT /api/admin/settings/{key}` cannot enable them. Restart the backend after
changing deployment environment variables.

| Configuration key | Environment variable | Default |
| --- | --- | --- |
| `Python:SandboxCommand` | `Python__SandboxCommand` | unset; refuse snippets |
| `Python:Executable` | `Python__Executable` | `python3` |
| `Python:AllowUnsandboxed` | `Python__AllowUnsandboxed` | `false` |

A snippet may name a **Python environment** (Admin → Python environments). It then runs
under that environment's interpreter, `/app/pyenv/envs/<name>/bin/python`, resolved by
the backend — never from workflow input. Everything else above and below applies
unchanged: the wrapper still goes in front, the isolation flags and scrubbed environment
are the same, and the import allowlist still decides what the snippet may import. A
wrapper must therefore make `/app/pyenv/envs` (read-only) and, for a pinned Python
version, `/app/pyenv/python` visible to the child.

Compose exposes the first and third via `PYTHON_SANDBOX_COMMAND` and
`PYTHON_ALLOW_UNSANDBOXED`. SandboxCommand is a semicolon-delimited executable and
argument list, without shell expansion. The first item must be an absolute path.
Nashira appends the Python executable and interpreter arguments, and feeds code on
stdin. A missing or failing wrapper refuses execution; it never falls back to
unsandboxed execution, even when AllowUnsandboxed is true.

Configure a deployment-owned wrapper, such as `/opt/nashira/bin/python-sandbox`,
which uses your OS isolation mechanism. Before enabling it, verify that it denies
backend files, Data Protection keys, host mounts and network access; gives the
child only its required runtime and temporary storage; and bounds CPU/memory.
The path must not be writable by snippet authors. An absolute path is input
validation, not proof of isolation: `/usr/bin/env` is **not** a sandbox. Nashira
cannot infer the security of an arbitrary wrapper. Test the wrapper in a disposable
environment before deployment. NetworkEnabled controls imports, not kernel access.

For explicitly trusted development only, setting `Python:AllowUnsandboxed=true`
permits an **authenticated Admin** to run without a wrapper. Both conditions are
required for every invocation; Operators, Viewers, anonymous and background runs
without an Admin identity are refused. This grants the child the backend user's
filesystem and network access and logs a warning. Never use it to fix production
availability. There is no API that silently enables this bypass.
