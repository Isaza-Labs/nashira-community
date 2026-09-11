# Skill: snippets — the seven step types a node can be

A **snippet** is the reusable unit a workflow node invokes by `snippet_id`. The node
supplies input through `config_overrides`; the snippet supplies the behaviour, the
timeout, the retry policy and the idempotency tier.

Three tools: `list_snippets` to browse the catalogue, `create_snippet` to add to it, and
`bulk_delete_snippets` to remove many at once (one confirmed call, one mutation-budget
slot; select by `snippet_ids`/`names` and/or filters `name_contains` / `type` — snippets
still referenced by an active workflow are skipped and reported with the workflows that
name them). All run in-process against the catalogue itself, so none depends on the
self-API spec being reachable. `create_snippet` deliberately does not expose everything a snippet
row holds: `network_enabled` is absent from its schema (it lifts the python sandbox's
network isolation and is a human act from the Snippets UI), and so is the retry policy.

Everything past create-and-list is the `na_snippets` spec through `execute_operation`:
`snippets_listTypes`, `snippets_get`, `snippets_update` (retry policy included),
`snippets_delete`, the vendor-command catalogue (`snippets_resolveVendorCommand`,
`snippets_listVendorCommands`, `snippets_createVendorCommand`, …) and the python
allowlist (`snippets_listPythonModules`, `snippets_addPythonModule`,
`snippets_retryPythonModule`).

**A workflow node cannot run a type — only a snippet.** `ping` in the table below is a
handler type; the node references the UUID of a snippet configured to use it. So the
order is always:

1. `list_snippets` — see what exists. It is ordered proven-first, and `proven=true`
   means a step using that snippet has actually completed here.
2. If nothing fits, `create_snippet` — it returns a `snippet_id`.
3. `create_workflow`, with that UUID as the node's `snippet_id`.

Building the workflow first and filling the reference in later does not work: the
reference is validated when the definition is written.

**Never invent a placeholder.** `__ping__` is not a thing. The only non-UUID values a
`snippet_id` accepts are `__start__`, `__end__` and `subflow`; anything else is rejected
with `snippet_reference_invalid`, and a workflow of nothing but `__start__` → `__end__`
is not a workflow — it is a graph that runs and does nothing. A fresh install already
carries a baseline snippet for every executable handler (their slugs start
`baseline-`), so `list_snippets` is never empty and there is never a reason to guess.
The two reserved stubs (`netconf`, `snmp_v3`) have no baseline on purpose: a seeded
snippet that can only fail would be a booby trap.

## The handlers

| `type` | What it does | Default idempotency |
|---|---|---|
| `ping` | TCP reachability probe against a device or literal IP. | `idempotent` |
| `transform` | Reshapes the step input via a `mapping` of output-name → path. Pure, no I/O. | `idempotent` |
| `rest_call` | Calls one catalogued API operation through the REST executor (resolves the spec, its base URL, the linked integration's credentials and `${secret:…}` refs). | `requires_compensation` |
| `integration_action` | Invokes a catalogued action on a registered integration. | `requires_compensation` |
| `ssh` | Runs CLI commands on an inventory device via netmiko. | `requires_compensation` |
| `mcp_call` | Invokes a tool on a registered MCP server. | `non_reversible` |
| `python_snippet` | Runs the author's Python against the step input, in the sandbox. | `non_reversible` |
| `git` | Reads, writes, commits, pulls and pushes a registered repository. | `requires_compensation` |
| `report` | Renders markdown `content` into a stored report (markdown/html/pdf), linked to the run in /reports; output includes `base64` for a downstream attachment. | `idempotent` |
| `email_send` | Sends mail through a named email channel (`channel` = slug or id) or the default relay; supports cc/bcc, html and base64 attachments. | `non_reversible` |
| `email_mailbox` | Operates on the inbound (IMAP) side of an email channel via `action`: `list`, `read`, `mark`, `move`, `archive`, `delete`, `folders`. A list step's `uids` output feeds the acting steps. | `requires_compensation` |
| `slack_message` | Posts `text` through a messaging channel (`channel` = slug or id — Slack, Teams or webhook); the delivery is recorded against the run. | `non_reversible` |
| `ansible_playbook` | Runs the snippet's YAML playbook against `device` (inventory, environment-gated) or a literal `host`. Linux worker with ansible-playbook only. | `non_reversible` |
| `netconf` | Reserved stub — fails with `not_implemented` until a NETCONF client lands. | `requires_compensation` |
| `snmp_v3` | Reserved stub — fails with `not_implemented` until an SNMP client lands. | `requires_compensation` |

`git` is `requires_compensation` rather than `non_reversible` on purpose. A push is
undone by a revert commit, which is a compensation an author can wire on the failure
edge — and making the type absolute would drag every `read_file` node into claiming it
can never be reversed. See the git skill for the operations and their fields.

`transform` is deliberately not an expression language: a `mapping` of output name →
path, resolved against `source` when the node supplies one and against the whole step
input when it does not. The mapping comes from the node's `config_overrides.mapping`, or
from the snippet's own `code` when that holds a JSON object — the second is what makes
one transform snippet reusable across nodes that supply only a `source`.

- A mapping value that is not a string is emitted as a literal.
- Paths that resolve nothing are reported in the step's error text while the step still
  succeeds — unless **none** of them resolved, which fails with `unresolved` rather than
  returning `{}` and pushing an authoring mistake downstream.
- No mapping anywhere is a pass-through of `source` (or of the whole input), not an
  error.

Anything richer belongs in a `python_snippet`.

`ping` treats unreachable as a *result* you branch on rather than a step failure — that
is the whole point of a ping node, and a failed step would skip the branch instead. It
does still fail on input it cannot act on: no `host`/`device` at all (`bad_input`), or a
name that is neither an IP nor a device in inventory (`not_found`).

## A snippet that carries CODE has to explain itself

`python_snippet`, `python`, `transform` and `jmespath` REQUIRE a `logic_diagram_mermaid`.
Create or update one without it and the API refuses with `logic_diagram_invalid`.

The reason is the one thing a type and a name cannot give you: a `ping` explains itself, a
`python_snippet` does not. The diagram is what lets someone opening the workflow six months
later understand what the step does without reading the script.

The check is deliberately shallow — the field must be present and must START with a Mermaid
directive (`graph`, `flowchart`, `sequenceDiagram`, `stateDiagram`, `classDiagram`, …). It is
there to catch a pasted script or an empty string, not to validate the diagram; the renderer
does that. `Skills/mermaid.md` has the conventions and worked examples.

**This applies to every type, not just the required ones**: a diagram you DO supply must be a
diagram. A field that accepts anything eventually holds a stack trace somebody pasted.

Adopted from FlowWeaver on 2026-08-30 so both products refuse the same payload with the same
message — a snippet authored here is now portable to a FlowWeaver instance by the route an
operator actually takes, which it was not before.

One asymmetry survives and is deliberate: a BUNDLE IMPORT does not enforce this, in either
product. An imported workflow may therefore carry a code-carrying snippet with no diagram. If
you are reviewing an imported workflow, that is where to look.

## Idempotency, and why it is not decoration

Three tiers, and the rollback planner reads them literally:

- `idempotent` — safe to replay with the same input.
- `requires_compensation` — has side effects; a `failure` edge must wire the reversal.
- `non_reversible` — no automatic compensation exists.

An unannotated node is treated as `requires_compensation`. A snippet may declare its own
tier, and the declaration wins **in either direction** over the handler's default — that
is the point, since only the author knows their `integration_action` wraps a GET and can
safely say `idempotent`, or wraps something the handler cannot see and should say
`non_reversible`.

The one exception is absolute: a `non_reversible` **handler** stays `non_reversible`
whatever the snippet declares. Those handlers are `email_send`, `slack_message`,
`ssh` and `ansible_playbook` — a delivered message cannot be recalled and a device
reconfigured over SSH cannot be un-reconfigured by re-running the step. A declaration to
the contrary would only make the rollback planner promise a reversal that does not exist.

`python_snippet` and `mcp_call` are **not** on that list, though they used to be. The
ceiling was lowered because it was doing the wrong job: it is absolute, so it silently
overrode an author who had said "this script is compensable" — and a failed run then
reported `failed` where the contract's oracle reports `rolled_back`. Their floor is now
`requires_compensation`, which an author may raise or lower like any other.

## Did it change anything — the second declaration, and this one can fail a step

A tier says whether an action **could** be undone. It does not say whether anything **was**
done, and until 2026-08 the executor answered the second question with the first: a step
whose handler said nothing was recorded as changed unless its tier was `idempotent`. Most
handlers are not, so most steps claimed a mutation nobody had observed — and the rollback
plan, the audit trail and `final_state` were all built on that.

The inference is gone. Every handler now answers, and most **measure** it: `rest_call` and
`integration_action` read the HTTP verb, `git` reads the commit sha, `ansible_playbook`
reads the `changed=` count in the play recap, `ping` and `transform` change nothing by
construction.

**Four types cannot know, because the author supplies the action**: `ssh`,
`python_snippet`, `ansible_playbook` when it defers, and `mcp_call` — MCP gives a tool a
name and a schema and no verb. For those the author declares, and **where they declare
depends on where the action lives**:

| The action lives in | Declare on | Key |
|---|---|---|
| the node (ssh commands, an mcp tool name) | the node's `config_overrides` | `"changes": true \| false` |
| the snippet's own code (a python script, a playbook) | the snippet | `changes_state` |

The node wins over the snippet, being more specific — the same `ssh` snippet runs
`show version` on one node and `configure terminal` on another.

**A step of one of those types with no declaration anywhere FAILS.** The message names the
snippet and the field to set. That is deliberate and it is louder than what it replaced: a
default here is a guess, and every run outcome downstream would inherit it.

So when you create a snippet of one of those four types, or wire a node that uses one, say
whether it changes anything. A read-only `ssh` node that polls `show version` is
`"changes": false`; one that pushes config is `true`. Only literal `true`/`false` counts —
a string `"true"` is not a declaration, and the step still fails.

Never declare `false` to make a run look clean. `false` means the rollback plan will not
list this node, and the plan is what someone reads after a failure.

`create_snippet` and `list_snippets` both return `effective_idempotency` — what the
executor will actually apply — and `create_snippet` returns `declared_idempotency`
alongside it, so a declaration the ceiling overruled is visible rather than silently
lost.

Never talk a user into marking a step idempotent to make a rollback plan look better.
The plan would then promise a reversal that does not exist.

## `python_snippet` and the allowlist

The sandbox is the boundary, but imports are checked before it starts:

- Every imported root module must be on the tenant's allowlist
  (Admin → Python modules, `/api/python-modules`). The failure names the offending
  module — relay that name, it is the only actionable part.
- A module marked `requires_network` is only available when the snippet has
  `network_enabled`. Otherwise the same error appears with "this snippet has not opted
  into network access".
- Dynamic import machinery (`__import__`, `importlib`, …) is **refused outright**, not
  sandboxed. A snippet whose imports cannot be checked is not run.
- Only an admin can widen the allowlist, and only root-module names are accepted.

### Credentials: `${secret:…}`, never a literal

Every string in the node's `config_overrides` is scanned for
`${secret:<source>:<name|id>:<field>}` and substituted immediately before the interpreter
starts — the same grammar `ssh` and `rest_call` use, and it reaches values nested inside
arrays and objects, not just the top level. A script that needs a device password takes
`"password": "${secret:credential:core-sw:password}"` and finds the plaintext in `input`.

Never put the plaintext in `config_overrides` instead: that is stored in the workflow
definition, travels in every bundle export, and is echoed back in the step's input
snapshot on every run. A reference that resolves to nothing is left **literal**, so a
mistyped credential name arrives as the marker text rather than as a silent empty string.

### Interactive SSH: start from the seeded baseline

For an exchange the `ssh` step cannot hold — a password change that prompts twice, a
`[confirm y/n]`, a commit-confirm — there is a seeded snippet,
**`SSH primitive (paramiko)`** (slug `baseline-ssh-paramiko`). Point a node at it and
configure it through `config_overrides` rather than writing a new script:

- `mode: "exec"` (default) — each entry of `commands` on its own channel, with
  `stdout` / `stderr` / `exit_status` per command.
- `mode: "shell"` — one interactive shell driven by `steps`, each `{send, expect}`. A step
  carrying a secret takes `"secret": true`, which keeps its payload out of the stored run
  output.
- `host` is required and there is no device object here: on a `per_device` node pass
  `{{ device.ip }}`.
- `ok: false` is a result to branch on — only connect/auth errors fail the step.

**It is seeded inert.** Before its first run an admin must approve `paramiko` (pip,
`requires_network`) and `io` (stdlib) under Admin → Python modules, then set
`network_enabled` on the snippet. The module-denial message names exactly those two fixes
— relay it rather than rewriting the script.

If you do write a fresh one, two refusals are invisible from reading the body, because the
scanner matches on TEXT and not on grammar: a helper named `_run_exec` trips
`\bexec\s*\(`, and anything that spells out a call to the builtin that turns source into
code — a comment included — is rejected. Use the module-level `re.search` / `re.sub`
instead of the pre-compiling form.

### The script's contract

Three things, and getting them wrong is the most common way a `python_snippet` fails for
a reason unrelated to what it was written to do:

- The node's resolved `config_overrides` arrive as a dict named **`input`**. Not
  `payload`, not `context`, not a function argument.
- Whatever the script assigns to **`result`** IS the step's output. No envelope: a
  downstream node reads `{{ steps.<node_id>.output.<field> }}` directly.
- A script that assigns nothing and **prints a JSON object** has that taken as the output
  instead. That is how scripts written against other implementations of this contract are
  authored, and they run here unchanged.
- Assigning **wins** when a script does both, so adding a `print` never changes what an
  already-written script means.
- A script that assigns nothing and prints nothing parseable produced no output, and that
  is not an error — a script whose purpose is its side effect is fine. Unparseable text it
  did print is kept under `raw`.

Anything else the script prints becomes the step's **logs**, which the run detail shows.
The platform appends its own marker to the end of the process output and strips it before
storing the logs, so you never see it and never have to work around it.

### Approved is not the same as installed

An allowlist row is one of two things, and the difference decides whether anything has
to happen before a snippet can import it:

| `source` | What the row is | Born as |
|---|---|---|
| `stdlib` | Permission only — the module is already on the interpreter. | `ready` |
| `pip` | Permission **and an install**. The platform runs `pip install` of `pip_spec` into a shared package directory. | `pending` |

A pip row moves `pending` → `installing` → `ready` or `failed`. **Only `ready` is
importable.** So there are three different answers to "why can't my snippet import
this?", and the error text distinguishes them:

- *not on the allowlist* — nobody approved it. An admin has to.
- *approved and still installing* — retry in a moment; nothing is wrong.
- *approved but its install failed* — the row carries pip's own output. An admin fixes
  the requirement and retries the install; re-running the workflow changes nothing.

Two things worth saying to a user who asks for a package:

- `pip_spec` and the module name routinely differ. `pip install pyyaml` imports as
  `yaml`; `beautifulsoup4` imports as `bs4`. Getting this wrong produces an install
  that succeeds and a module that does not import, which the platform catches and
  reports rather than marking ready.
- Approving a pip package ends with third-party code running wherever snippets run. It
  is the most privileged thing on that screen, it is audited with the exact version pin,
  and it is an admin decision — describe it that way rather than as a formality.

A failed script returns `error_kind`. Only `timeout` is retried — a syntax or import
error fails identically forever.

## `ssh` and destructive commands

Commands are classified per command, independently of the tool-level confirmation:

Classification is by pattern over the command text, most severe first:

- **destructive** — `reload` or `reboot` anywhere in the command; `write erase`; `erase`
  of `startup-config` / `nvram` / `flash` / `config`; a command **starting** with
  `erase`, `delete` or `format`; `clear config`; `factory-reset` / `factory reset`;
  `zeroize`.
- **mutation** — a command starting with `configure` / `conf t` / `config`, `set`,
  `write` / `wr`, `shutdown` or `no shutdown`; `copy running-config startup-config`;
  `write memory`; `commit` anywhere.
- **read** — a command starting with `show`, `sh`, `display`, `disp`, `get`, `dir`,
  `ping`, `traceroute`, `monitor`, `more` or `cat`.
- **An unrecognised command is a mutation**, not a read.

`delete` and `format` are destructive, not mutations: `delete flash:image.bin` is
blocked by the same setting that blocks `reload`.

Destructive commands are blocked unless an admin has set `Ssh:AllowDestructiveCommands`.
When one is blocked, say which command and that it needs a platform setting change — do
not look for a phrasing that slips past the classifier.

The step also enforces the run's environment against the device's `allow_draft` /
`allow_qa` / `allow_production` trio, and host-key pinning applies (see the inventory
skill).

## Multi-vendor without branching

The vendor-command catalogue maps `intent` + `platform` → `command`, with an optional
`parser_template` and a `read_only` flag. Resolve an intent through
`GET /api/vendor-commands/resolve` (`snippets_resolveVendorCommand` in the `na_snippets`
spec) instead of hard-coding `show version` per vendor into seven parallel nodes. Pass
either `platform` or `device_id` — with a device id the platform is read from inventory,
which is the form to prefer inside a workflow.

The `intent` is a vendor-neutral name, not a command: `show_ip_interfaces` resolves to
`show ip interface brief` on `cisco_ios`, `show interfaces terse` on `juniper_junos` and
`show interface brief` on `nokia_srl`.

A fresh install ships a seeded catalogue for `cisco_ios`, `cisco_xe`, `cisco_xr`,
`cisco_nxos`, `cisco_asa`, `juniper_junos`, `arista_eos`, `nokia_sros`, `nokia_srl`,
`huawei`, `fortinet` and `linux`. Every one of them answers `show_version`,
`show_interfaces` and `show_route_table`; most also carry `show_running_config`,
`show_ip_interfaces`, `show_arp`, `show_lldp_neighbors`, `show_bgp_summary`,
`show_ospf_neighbors`, `show_inventory`, `show_cpu`, `show_memory`, `show_logs`,
`show_clock`, `show_users` and `show_environment`, plus `show_mac_table` and
`show_vlans` on the switching platforms. `save_config` is the one seeded entry with
`read_only: false`, and it is absent where persistence is a config-mode commit rather
than an operational command (`cisco_xr`, `juniper_junos`).

Two ways this misses, and both are answers rather than problems:

- **`aruba_aoscx`, `aruba_osswitch`, `hp_comware`, `mikrotik_routeros`, `risecom_ros`
  and `generic` ship no catalogue.** The SSH runner supports them; their command sets
  have not been verified. Resolving on those platforms 404s on purpose — say the
  platform has no catalogue entry and offer to add one with
  `snippets_createVendorCommand`, rather than guessing syntax from a neighbouring vendor.
- **A resolve that 404s for a seeded platform means that intent is not in the
  catalogue**, not that the device cannot do it. Add the entry rather than falling back
  to a literal command in the node — the next workflow gets it for free.

Never invent an intent name in a workflow and assume it resolves. Read the catalogue
first with `snippets_listVendorCommands`, filtered by `platform` or `intent`.

## Moving snippets between instances

A snippet's `slug` is its cross-instance identity, assigned once at creation and never
recomputed — renaming a snippet here does not break a bundle already shared.

`GET /api/workflows/{id}/bundle` exports a workflow together with the full definition of
every snippet its nodes name; `POST /api/workflows/import` takes that file back. On the
receiving instance each snippet is matched by slug, then by exact name, and recreated
from the definition that travelled when neither matches. Two things the import will not
do, both deliberate:

- **It will not grant `network_enabled`.** A snippet that had it on the source arrives
  without it, and the response says so in `import_notes`. Lifting the sandbox's network
  isolation is an admin act performed here, not something a file can carry in.
- **It will not invent a missing dependency.** An integration or MCP server the workflow
  names must already exist locally, with its own credentials; the refusal lists exactly
  what is absent. A fabricated stub would produce a run that goes green having done no
  work, which is worse than a rejected import.

A bundle whose snippets need a handler this build does not have (FlowWeaver ships
`ansible`, `netconf`, `snmp_v3` and others) is refused at import with the offending type
named, rather than stored and left to fail on a device.
