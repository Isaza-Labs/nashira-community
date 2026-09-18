import type { DocSection } from '../types';

export const workflows: DocSection[] = [
	{
		slug: 'workflows',
		title: 'Workflows',
		group: 'Workflow platform',
		module: 'automation',
		tagline: 'The reviewable artifact a bulk or irreversible change becomes — and the way a routine gets scheduled.',
		uiPath: '/workflows',
		apiBase: '/api/workflows',
		role: 'Viewer',
		purpose:
			'A directed graph of snippets that says what to do, in what order, on which devices, and under what conditions — as data, so it can be reviewed, diffed, tested, promoted and replayed before and after it touches anything. The engine executes it deterministically; no language model is in the execution path, and there is no AI step type. In the console it lives under Build → Workflows.',
		keywords:
			'automation dag graph nodes edges yaml import export run promote simulate bundle portable interchange flowweaver flow weaver subflow',
		blocks: [
			{
				kind: 'params',
				title: 'Workflow fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Display name.' },
					{ name: 'description', type: 'string | null', desc: 'Free text. Policies can match on it via `description_contains`.' },
					{ name: 'nodes', type: 'array', required: true, desc: 'The steps. See the node shape below.' },
					{ name: 'edges', type: 'array', required: true, desc: 'The order and the branching. See the edge shape below.' },
					{ name: 'input_schema', type: 'object | null', desc: 'JSON Schema for the run input, so a caller learns what the workflow expects.' },
					{ name: 'metadata', type: 'object | null', desc: 'Free-form authoring metadata, such as labels. Canvas positions live on each node as `x`/`y`.' },
					{ name: 'change_summary', type: 'string', desc: 'Why this edit was made. Carried into the version snapshot on promotion.' },
					{ name: 'conversation_id', type: 'uuid | null', desc: 'The chat this workflow came out of, when the agent drafted it. Must name an existing conversation, or the write is refused.' },
					{ name: 'environment', type: 'string', default: 'draft', desc: 'Read-only here — changed only by promotion.' },
					{ name: 'version', type: 'number', desc: 'Read-only. Incremented on promotion.' },
					{ name: 'promoted_from', type: 'uuid | null', desc: 'Read-only. The workflow this row was promoted from — a promotion creates a new row.' },
					{ name: 'schema_version', type: 'string', desc: 'Read-only. The workflow format, currently `v1`.' },
					{ name: 'schema_hash', type: 'string', desc: 'Read-only. Canonical fingerprint of nodes + edges; two workflows with the same hash are the same graph.' },
					{ name: 'last_simulation_id', type: 'uuid | null', desc: 'Read-only. Kept after an edit: promotion compares the simulated schema hash with the current one and refuses a stale result with 412 `simulation_stale`. Import and restore start with none.' }
				]
			},
			{ kind: 'heading', text: 'Node shape' },
			{
				kind: 'code',
				caption: 'One node',
				text: `{
  "id": "check-reachable",          // unique within the workflow
  "snippet_id": "8f0c…",            // which Snippet runs here
  "type": "task",                   // task | decision | subflow, default "task"
  "x": 120, "y": 40,                // optional canvas position
  "config_overrides": {             // inputs for this snippet, templated
    "host": "{{ device.ip_address }}",
    "count": "{{ input.probe_count }}"
  }
}`
			},
			{
				kind: 'list',
				items: [
					'`snippet_id` is a snippet id, or one of the literals `__start__`, `__end__` (markers that do nothing) and `subflow`.',
					'Nodes and edges accept only the keys shown — `id`, `snippet_id`, `type`, `x`, `y`, `config_overrides` on a node; `source`, `target`, `type`, `condition`, `source_handle`, `target_handle` on an edge. Anything else fails workflow.v1 validation.',
					'Two keys in `config_overrides` are read by the engine rather than passed as input: `changes` (`true`/`false`, whether the step changed anything — see **Runs**) and `idempotency` (raises the snippet\'s tier, never lowers it — see **Snippets**).'
				]
			},
			{
				kind: 'code',
				caption: 'A subflow node — runs another workflow as a child run',
				text: `{
  "id": "provision-vlan",
  "snippet_id": "subflow",
  "type": "subflow",
  "config_overrides": {
    "subflow_workflow_id": "3b1e…",   // required; the workflow to run
    "vlan": "{{ input.vlan }}"         // every other key is the child's input
  }
}`
			},
			{
				kind: 'prose',
				text: 'A subflow node starts a real run of its own: its own row with `trigger: subflow` and `parent_run_id`, its own steps and audit events, and the parent step records it as `child_run_id`. The child\'s input is the parent run\'s input merged with the node\'s other `config_overrides` keys, resolved in the parent\'s context. A node without a valid `subflow_workflow_id` fails with `subflow_missing`; nesting deeper than 8 levels fails with `subflow_depth_exceeded`, and a cycle with `subflow_cycle`.'
			},
			{ kind: 'heading', text: 'Edge shape' },
			{
				kind: 'code',
				caption: 'Four edge types — every edge needs a type',
				text: `{ "source": "a", "target": "b", "type": "success" }
{ "source": "a", "target": "rollback", "type": "failure" }
{ "source": "a", "target": "notify", "type": "always" }
{ "source": "a", "target": "c", "type": "conditional",
  "condition": "{{ steps.a.output.changed }} == true && {{ input.apply }} == true" }`
			},
			{
				kind: 'values',
				title: 'Edge types',
				rows: [
					{ value: 'success', desc: 'Follow when the source step succeeded. There is no default: an edge without `type` fails workflow.v1 validation and the write is refused.' },
					{ value: 'failure', desc: 'Follow when it failed — the branch for cleanup or rollback.' },
					{ value: 'always', desc: 'Follow whatever the source step returned, success or failure.' },
					{ value: 'conditional', desc: 'Follow when `condition` evaluates true. An unresolvable or malformed condition is false, never true. An edge with **no** `condition` is refused when a run starts, before any step executes, naming the edge — simulation reports it earlier as `conditional_missing_condition`.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'References in a condition need braces',
				text: 'A `condition` uses the template grammar, so every reference goes inside `{{ }}`. Written bare, `steps.a.output.changed` is compared as literal text: the condition is always false and the branch silently never runs. Supported: `== != >= <= > <`, `&&`, `||`, parentheses, a bare `{{ ref }}` read for truthiness, and filters such as `{{ ref | strip | lower }}`.'
			},
			{ kind: 'heading', text: 'Templates' },
			{
				kind: 'prose',
				text: 'Any string in `config_overrides` may reference earlier state: `{{ input.x }}` (the run input), `{{ device.ip_address }}` (the current target in a per-device step), `{{ steps.<node_id>.output.<path> }}` (an earlier step\'s output, with `a.b[0].c` paths) and `{{ run.<field> }}` (the run itself: `id`, `workflow_id`, `workflow_name`, `environment`, `trigger`, `started_at`, `failed_step_id`, `failed_step_error`, …).'
			},
			{
				kind: 'prose',
				text: 'A reference can carry filters: `{{ steps.ssh.output.stdout | strip | lower }}`. Available: `trim`, `upper`, `lower`, `truncate(n)`, `json`, `strip_ansi`, `strip` and `default(\'x\')`, which is the only one that stands in for a missing, null or empty value. Unknown filters are ignored.'
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'An unresolved reference fails the step',
				text: 'If a `{{ … }}` is still there after resolution — a typo in a node id, a path that does not exist, `{{ device.* }}` in a `once` step — the step fails with `unresolved_template`, naming the field, before its handler runs. It is never sent as literal text or as null.'
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'Whole-string vs inline',
				text: 'A value that is exactly one placeholder keeps its JSON type — `"{{ input.count }}"` yields the number `3`. A placeholder inside a longer string is stringified — `"host-{{ input.count }}"` yields `"host-3"`.'
			},
			{ kind: 'heading', text: 'Lifecycle' },
			{
				kind: 'steps',
				items: [
					'**Author** — build the graph in the console, import YAML, or ask the agent. Only a draft is editable.',
					'**Simulate** — validate the graph, compute execution order and report issues and warnings without touching a device.',
					'**Run** — execute against real targets, with input and an optional device list.',
					'**Promote** — draft → qa → production, subject to four-eyes and any gate policies. A version snapshot is written in the same transaction.'
				]
			},
			{
				kind: 'endpoints',
				title: 'Authoring',
				rows: [
					{ method: 'GET', path: '/api/workflows', role: 'Viewer', desc: 'List. Filter with `environment` (`draft`, `qa` or `production`). The console shows one tab per environment, kept in the URL as `/workflows?env=qa`, and can delete a multi-row selection — one audited soft delete per row.' },
					{ method: 'GET', path: '/api/workflows/{id}', role: 'Viewer', desc: 'Full workflow including nodes and edges.' },
					{ method: 'POST', path: '/api/workflows', role: 'Operator', desc: 'Create. Validates against workflow.v1 and requires an acyclic graph.' },
					{ method: 'PUT', path: '/api/workflows/{id}', role: 'Operator', desc: 'Update a draft. A structural edit clears every acceptance-test verdict; the simulation link is kept and judged stale at promotion.' },
					{ method: 'DELETE', path: '/api/workflows/{id}', role: 'Operator', desc: 'Soft delete.' },
					{ method: 'GET', path: '/api/workflows/{id}/yaml', role: 'Viewer', desc: 'Compile to YAML. `?download=true` returns it as a file.' },
					{ method: 'GET', path: '/api/workflows/{id}/bundle', role: 'Viewer', desc: 'Export the portable bundle (v3 JSON). `?download=false` returns it inline.' },
					{ method: 'POST', path: '/api/workflows/import', role: 'Operator', desc: 'Import a YAML artifact or a bundle. Body `{ content, name, change_summary }` — `content` is the file text, `name` overrides the name inside it. A `kind` of `flow_weaver.workflow_bundle`, `nashira.workflow_bundle` or `netora.workflow_bundle` marks a bundle; anything else is read as YAML/JSON.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'Import always creates a new draft',
				text: 'The `id` and `environment` in the file are ignored on purpose. Otherwise an imported file could overwrite a local workflow, or arrive already marked production and skip the promotion gate entirely.'
			},
			{ kind: 'heading', text: 'Bundles: moving a workflow between instances' },
			{
				kind: 'prose',
				text: 'The YAML export names this instance\'s snippets, integrations and repositories by GUID. On a second instance none of those ids exist, so the import is refused by the same reference check that guards every write — which is why a YAML file only ever comes home. The **bundle** is the portable form: the same nodes and edges, plus everything needed to make their references mean something on the other side. Nashira and FlowWeaver read and write the same format (`workflow-v1-conformance/bundle/SPEC.md`, schema v3), so a workflow authored in either runs in the other. Export writes schema v3; import also accepts v2.'
			},
			{
				kind: 'params',
				title: 'What travels in a bundle',
				rows: [
					{ name: 'requires', type: 'object', desc: 'What the receiving instance must support: `snippet_types`, `capabilities` (a closed vocabulary: `subflow`, `template_filters`, `run_namespace`, `per_device_scope`, `conditional_edges` and `triggers` are supported; `max_parallel`, `per_pool` and `python_network` are read past with a note — steps run serially, a pool fans out per device, network access is never granted) and the `${secret:…}` references the workflow resolves at run time. Checked before anything is created.' },
					{ name: 'workflow', type: 'object', desc: 'Name, description, input schema and metadata. The source `workflow_id`, version, schema hash, runs, tests and promotion history do not travel.' },
					{ name: 'nodes / edges', type: 'array', desc: 'Verbatim workflow.v1, with every outside reference written as a portable key: `integration` (slug), `action`, `server`, `credential`, `repository` — names, not local ids.' },
					{ name: 'dependencies.snippets', type: 'array', desc: 'The full definition of every snippet the nodes name — code, schemas, target mode, timeout, idempotency, retry policy. Recreated on the far side when it is not already there, always as unverified and never network-enabled.' },
					{ name: 'dependencies.workflows', type: 'array', desc: 'Every sub-workflow reachable through a `subflow` node, transitively, once each. Created first on import, then every `subflow_workflow_id` is remapped.' },
					{ name: 'dependencies.integrations / mcp_servers / credentials / repositories', type: 'array', desc: 'Identity only — name, slug, type, base or remote URL. Never auth config, headers, tokens, private keys or webhook secrets. The receiving instance must already hold its own credentials for those systems.' },
					{ name: 'triggers', type: 'array', desc: 'Cron and webhook triggers without their HMAC secret, their target devices or their statistics. Created disabled, with a fresh secret and no targets.' }
				]
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'Secrets never travel; references do',
				text: 'A bundle is safe to attach to an email. What it carries about a credential is its name, so the node can find the local one; a `${secret:store:name:field}` reference inside `config_overrides` is kept exactly as written and listed in `requires.secrets`, so the operator can create the ones this instance lacks before the first run. No encrypted material, no token, no key, no webhook secret is ever written to the file.'
			},
			{
				kind: 'values',
				title: 'Import outcome — exactly one of two',
				rows: [
					{ value: 'refused', desc: 'Nothing is created, and the error names every problem at once: `bundle_dependencies_missing`, `bundle_capability_unsupported`, `bundle_reference_untranslatable`, `bundle_subflow_cycle`, `bundle_incomplete`, `bundle_version_unsupported`, `bundle_parse_failed`, `bundle_empty`. Nothing is guessed and nothing is stubbed — a placeholder that returns nothing would turn a shared workflow into a run that goes green having done no work.' },
					{ value: 'imported with notes', desc: 'The workflow, its sub-workflows, snippets and triggers exist. `import_notes[]` lists every degradation and everything still to configure — a secret that does not resolve here, a trigger waiting for its targets, a snippet whose network flag was dropped, a `per_pool` fan-out read as `per_device`. Silence means nothing was degraded.' }
				]
			},
			{
				kind: 'prose',
				text: 'The response also carries `created_snippets`, `created_workflows` and `created_triggers`, so a reviewer can open what arrived before the workflow is promoted. The import lands in `draft` at version 1 with no simulation evidence, and passes the same schema, reference and DAG validation as a hand-authored write.'
			},
			{
				kind: 'endpoints',
				title: 'Validation, execution and history',
				rows: [
					{ method: 'POST', path: '/api/workflows/{id}/simulate', role: 'Operator', desc: 'Dry run: order, issues, warnings, schema hash.' },
					{ method: 'GET', path: '/api/workflows/{id}/plan', role: 'Viewer', desc: 'Execution order plus whether the graph is fully reversible and which nodes are not.' },
					{ method: 'POST', path: '/api/workflows/{id}/run', role: 'Operator', desc: 'Execute. Body: `{ input, target_devices }`.' },
					{ method: 'GET', path: '/api/workflows/{id}/runs', role: 'Viewer', desc: 'Run history for this workflow.' },
					{ method: 'GET', path: '/api/workflows/runs/{runId}', role: 'Viewer', desc: 'The full run with every step payload inline. See **Runs** for the lighter `/api/runs/{runId}`.' },
					{ method: 'POST', path: '/api/workflows/{id}/promote', role: 'Operator', desc: 'Advance one environment. Body `{ target, approved_by, change_summary }`, with `target` set to `qa` or `production`. Refusals are 412: `simulation_missing`, `simulation_failed` or `simulation_stale` (draft → qa needs a passing simulation of the current graph), `policy_blocked`, or `approval_required` (qa → production needs an `approved_by` other than the person promoting). The promoted workflow is a **new row** with its own id and `promoted_from` pointing at the source, which is left as it was.' },
					{ method: 'GET', path: '/api/workflows/{id}/versions', role: 'Viewer', desc: 'Promotion snapshots.' },
					{ method: 'POST', path: '/api/workflows/versions/{versionId}/restore', role: 'Operator', desc: 'Restore a snapshot as a new draft.' }
				]
			}
		]
	},
	{
		slug: 'runs',
		title: 'Runs',
		group: 'Workflow platform',
		module: 'automation',
		tagline: 'What actually happened, step by step.',
		uiPath: '/runs/{id}',
		apiBase: '/api/runs',
		role: 'Viewer',
		purpose:
			'A run is the record of one execution: which targets, what each step returned, what changed, and whether the whole thing could be rolled back. It is the evidence a promotion gate points at.',
		keywords: 'execution history steps results rollback changed failed status',
		blocks: [
			{
				kind: 'params',
				title: 'Run fields',
				rows: [
					{ name: 'status', type: 'string', desc: '`running`, `completed` or `failed`.' },
					{ name: 'final_state', type: 'string', desc: '`completed`, `rolled_back`, `failed` (stopped part-way, changes may be left behind) or `refused` (a policy, the graph or the targets stopped it before any step ran — the run still gets a row).' },
					{ name: 'trigger', type: 'string', desc: 'What started it: `manual`, `agent`, `schedule`, `webhook`, `git_webhook`, `test` or `subflow`.' },
					{ name: 'error', type: 'string | null', desc: 'Why the run failed when no step can say — a policy denial, a graph that does not run, targets that do not resolve.' },
					{ name: 'parent_run_id', type: 'uuid | null', desc: 'The run whose `subflow` node started this one.' },
					{ name: 'environment', type: 'string', desc: 'The environment the workflow was in when it ran.' },
					{ name: 'node_count / changed_count / failed_count', type: 'number', desc: 'Steps executed, steps that changed something, steps that failed.' },
					{ name: 'input', type: 'object | null', desc: 'The input the run was given.' },
					{ name: 'target_devices', type: 'uuid[]', desc: 'The device ids the run was given; pools are not expanded here. Never silently narrowed: an unknown or inactive device, or one not allowed in this environment, refuses the whole run.' },
					{ name: 'rollback_plan', type: 'object', desc: 'What could be undone, and which nodes cannot be.' },
					{ name: 'started_at / finished_at', type: 'timestamp', desc: 'Wall clock.' }
				]
			},
			{
				kind: 'params',
				title: 'Step fields',
				rows: [
					{ name: 'node_id', type: 'string', desc: 'Which node in the graph.' },
					{ name: 'sequence', type: 'number', desc: 'Execution position.' },
					{ name: 'result', type: 'string', desc: '`changed`, `no_change`, `failed` or `skipped`.' },
					{ name: 'error_code / error', type: 'string | null', desc: 'Machine-readable failure reason, and the message.' },
					{ name: 'retryable', type: 'boolean', desc: 'Whether the failure was transient in the handler\'s judgement.' },
					{ name: 'attempts', type: 'number', desc: '0 when the step never reached a handler, 1 normally, more when it was retried.' },
					{ name: 'started_at / finished_at / duration_ms', type: 'various', desc: 'Timing of the step.' },
					{ name: 'input', type: 'object | null', desc: 'The configuration the handler received: templates resolved, secret-looking values redacted, capped at 4,000 characters.' },
					{ name: 'output / logs', type: 'various', desc: 'What the step returned, and the handler\'s own account of what it did.' },
					{ name: 'child_run_id', type: 'uuid | null', desc: 'The run a `subflow` step started.' }
				]
			},
			{
				kind: 'endpoints',
				title: 'Reading runs',
				rows: [
					{ method: 'GET', path: '/api/runs', role: 'Viewer', desc: 'Every run across every workflow. Filters (`status`, `environment`, `final_state`, `workflow_id`, `q`, and `from`/`to` on the start time) are applied server-side, so the total describes the same set as the rows. Paged with `limit` and `offset`.' },
					{ method: 'GET', path: '/api/runs/{runId}', role: 'Viewer', desc: 'One run with its workflow name and step headers — results, timings, errors and payload sizes, but no payloads. What the run page paints first.' },
					{ method: 'GET', path: '/api/runs/{runId}/steps/{sequence}', role: 'Viewer', desc: 'One step\'s output, resolved input and logs. Fetched when its card is opened.' },
					{ method: 'GET', path: '/api/workflows/runs/{runId}', role: 'Viewer', desc: 'The same run with every step payload inline. Heavier; used right after a run is started, when the steps are the point.' }
				]
			},
			{ kind: 'heading', text: 'Retries' },
			{
				kind: 'note',
				tone: 'primary',
				title: 'Two gates, both must open',
				text: 'A step is retried only when the failure is **retryable** and the step is **idempotent**. A non-idempotent step that fails halfway is left alone — repeating it is how one failed change becomes two.'
			},
			{
				kind: 'code',
				caption: 'A snippet\'s retry_policy',
				text: `{ "max_retries": 2, "initial_delay_seconds": 5,
  "backoff": "exponential", "max_delay_seconds": 300 }`
			},
			{
				kind: 'prose',
				text: '`max_retries` counts the attempts after the first. `backoff` is `fixed`, `linear` or `exponential`. Nashira caps every policy at 5 attempts and 30 seconds between them, whatever it asks for. A snippet gets a policy today only from a **bundle** import — the snippet API does not accept `retry_policy` yet, and a snippet without one is never retried.'
			},
			{ kind: 'heading', text: 'Per-device fan-out' },
			{
				kind: 'prose',
				text: 'A snippet with `target_mode: per_device` runs once per target and aggregates into `{ per_device, total, failed, devices[] }`. The aggregate reports "changed" if any device changed, and "no change" when every device says so.'
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'Where the handler cannot tell, the author must',
				text: 'Some handlers cannot know whether a step changed anything: `ssh` and `mcp_call` see only a command or a tool name, and `python_snippet` sees only a script. For those, the node\'s `config_overrides.changes` (`true` or `false`) decides, then the snippet\'s `changes_state`. With neither declared, the step fails with `change_undeclared`. This applies to `once` and `per_device` steps alike, and the idempotency tier is never used to guess.'
			}
		]
	},
	{
		slug: 'snippets',
		title: 'Snippets',
		group: 'Workflow platform',
		module: 'automation',
		tagline: 'The kinds of step a workflow node can be.',
		uiPath: '/admin/snippets',
		apiBase: '/api/snippets',
		role: 'Viewer',
		purpose:
			'A snippet is one reusable unit of work — ping a host, call a REST endpoint, run a command over SSH, transform data. Workflows compose snippets; nothing else is executable. In the console they live under Build → Snippets.',
		keywords: 'steps handlers ssh rest transform python mcp ping integration action idempotency',
		blocks: [
			{
				kind: 'values',
				title: 'Types',
				rows: [
					{ value: 'ping', desc: 'ICMP-style reachability check against a host.' },
					{ value: 'rest_call', desc: 'Arbitrary HTTP call, guarded against SSRF.' },
					{ value: 'transform', desc: 'Pure data reshaping between steps. No I/O, always idempotent.' },
					{ value: 'ssh', desc: 'Run a command on a device using its stored credential.' },
					{ value: 'integration_action', desc: 'Invoke a catalogued operation on a configured integration — auth and base URL come from the integration.' },
					{ value: 'mcp_call', desc: 'Call a tool on a configured MCP server.' },
					{ value: 'python_snippet', desc: 'Run Python in a restricted sandbox. Admin-gated network access, module allowlist. The step output is the value the script assigns to `result`; a script that assigns nothing may print JSON instead, and unparseable printed text lands under `raw`. Printed text always goes to the step logs.' },
					{ value: 'git', desc: 'Read, write, commit, pull or push a registered repository from inside a run — so a workflow that needs Git does not have to come back to a person.' },
					{ value: 'report', desc: 'Render markdown into a stored report (markdown/html/pdf), linked to the run in Reports. `csv` and `xlsx` export the tables in the markdown instead — one sheet per table — and fail with `not_supported` when there are none. The output carries the file as base64, ready to attach downstream.' },
					{ value: 'email_send', desc: 'Send mail through a named email channel or the default relay — cc/bcc, HTML and base64 attachments. Non-reversible.' },
					{ value: 'email_mailbox', desc: 'Operate on the inbound (IMAP) side of an email channel: list, read, mark read/unread, move, archive or delete messages. A list step\'s `uids` output feeds the acting steps.' },
					{ value: 'slack_message', desc: 'Post text to Slack. A Slack **messaging channel** (bot token) is used when one exists — the only path that can reply into a thread. Otherwise it goes through a **notification channel** (Slack, Teams or webhook; pick one with `via`), which refuses a threaded post with `not_supported` rather than flattening it. The delivery is recorded against the run. Non-reversible.' },
					{ value: 'ansible_playbook', desc: 'Run the snippet’s YAML playbook against an inventory device (environment-gated) or a literal host. Linux worker with ansible-playbook only.' },
					{ value: 'netconf / snmp_v3', desc: 'Reserved stubs: the types exist so shared workflows validate, and fail with not_implemented until a client library lands.' }
				]
			},
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Unique; also generates the slug.' },
					{ name: 'slug', type: 'string', desc: 'Read-only. Stable: renaming the snippet does not change it.' },
					{ name: 'type', type: 'string', required: true, desc: 'One of the types above that this deployment runs (see `GET /api/snippets/types`). Decides which handler executes it.' },
					{ name: 'description', type: 'string | null', desc: 'What the step does, for whoever reads the workflow later.' },
					{ name: 'code', type: 'string | null', desc: 'The body — a command for `ssh`, an expression for `transform`, a script for `python_snippet`.' },
					{ name: 'script_language', type: 'string | null', desc: 'Language hint for the editor.' },
					{ name: 'input_schema', type: 'JSON string | null', desc: 'What `config_overrides` should provide. Drives the authoring form.' },
					{ name: 'output_schema', type: 'JSON string | null', desc: 'What the step returns, so later steps can template against it.' },
					{ name: 'target_mode', type: 'string', default: 'once', desc: '`once` runs a single time; `per_device` fans out across the run targets.' },
					{ name: 'timeout_seconds', type: 'number', default: '60', desc: 'Per-execution timeout. Zero or a negative value means the default.' },
					{ name: 'idempotency', type: 'string | null', desc: 'Author\'s declaration: `idempotent`, `requires_compensation` or `non_reversible`. Null inherits the handler\'s default.' },
					{ name: 'effective_idempotency', type: 'string', desc: 'Read-only. What the engine will actually use — see the note.' },
					{ name: 'network_enabled', type: 'boolean', default: 'false', desc: '`python_snippet` only. Enabling is an Admin act; disabling any operator can do.' },
					{ name: 'verified', type: 'boolean', desc: 'Update only. Marks a snippet as reviewed — provenance, not a gate: an unverified snippet still runs.' },
					{ name: 'is_active', type: 'boolean', desc: 'Update only.' },
					{ name: 'changes_state', type: 'boolean | null', desc: 'Whether a step running this snippet changes anything. Needed for `python_snippet`, whose handler cannot tell: without it, or `changes: true|false` in the node\'s `config_overrides` (which wins), the step fails with `change_undeclared`.' },
					{ name: 'logic_diagram_mermaid', type: 'string | null', desc: 'Mermaid diagram of the step\'s logic. **Required** for `python_snippet` and `transform`. When present on any type, its first non-comment line must be a Mermaid directive (`graph TD`, `flowchart`, `sequenceDiagram`…), or the write is refused with `logic_diagram_invalid`.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'Non-reversible is a ceiling nobody can lower',
				text: 'Each handler declares a default tier. Where that default is `non_reversible` — `ssh`, `ansible_playbook`, `email_send`, `slack_message` — no declaration changes it, so nobody can mark an SSH configuration change "idempotent" and quietly enable retries on it. Below that ceiling, the author\'s `idempotency` replaces the default in either direction: `rest_call` defaults to `requires_compensation` because the handler cannot tell a GET from a DELETE, and an author who wrapped a read may declare it `idempotent`, which also allows retries. A node can raise the tier further with `config_overrides.idempotency`, never lower it. `effective_idempotency` is what the engine uses.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/snippets/types', role: 'Viewer', desc: 'The names of the types this deployment can execute, sorted. The list follows the enabled modules — see the note below.' },
					{ method: 'GET', path: '/api/snippets', role: 'Viewer', desc: 'List, filterable by `type` and `q`. Snippets of a type this deployment cannot run are left out, and their detail answers 404.' },
					{ method: 'GET', path: '/api/snippets/{id}', role: 'Viewer', desc: 'One snippet with its schemas.' },
					{ method: 'POST', path: '/api/snippets', role: 'Operator', desc: 'Create.' },
					{ method: 'PUT', path: '/api/snippets/{id}', role: 'Operator', desc: 'Update. Turning `network_enabled` on requires Admin.' },
					{ method: 'DELETE', path: '/api/snippets/{id}', role: 'Operator', desc: 'Soft delete. Refused with 409 `snippet_in_use` while an active workflow still names it.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Which types a deployment offers',
				text: '`ping`, `transform`, `rest_call` and `python_snippet` come with the workflow engine. The rest need a module too: `ssh`, `ansible_playbook`, `netconf` and `snmp_v3` need **fleet**; `mcp_call` and `integration_action` need **integrations**; `git` needs **git**; `report` needs **artifacts**; `email_send`, `email_mailbox` and `slack_message` need **communications**. A type whose module is off is neither listed nor accepted.'
			},
			{ kind: 'heading', text: 'The Python sandbox' },
			{
				kind: 'list',
				items: [
					'Modules are an **allowlist**, managed under Govern → Python modules. Nothing network-capable is seeded.',
					'The allowlist applies to the imports the snippet itself writes. An allowed module\'s own dependencies are not checked — the admin vetted the module, not how it is implemented.',
					'The child process starts with a scrubbed environment — connection strings and signing keys are not there to read.',
					'The interpreter runs isolated (`-I -B`), with an import hook enforcing the allowlist at runtime, not just a static scan.'
				]
			},
			{
				kind: 'note',
				tone: 'error',
				title: 'The sandbox is not a jail',
				text: 'There is no OS-level isolation unless you configure `Python:SandboxCommand` to wrap execution in bwrap, firejail or a container. Without it, authoring a `python_snippet` is close to having a shell on the host — treat the permission accordingly.'
			}
		]
	},
	{
		slug: 'triggers',
		title: 'Triggers',
		group: 'Workflow platform',
		module: 'automation',
		tagline: 'Start a workflow on a schedule, or from an external system.',
		uiPath: '/workflows/{id} → Triggers',
		apiBase: '/api/workflows/{workflowId}/triggers',
		role: 'Viewer',
		purpose:
			'Two ways to run a workflow without a person clicking: cron on a real timezone, and an authenticated inbound webhook. Both enqueue a job rather than executing inline.',
		keywords: 'cron schedule webhook hmac automation timer inbound',
		blocks: [
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'How the trigger appears in the list.' },
					{ name: 'type', type: 'string', default: 'cron', desc: '`cron` or `webhook`. Not editable after creation.' },
					{ name: 'description', type: 'string | null', desc: 'What the trigger is for.' },
					{ name: 'cron_expression', type: 'string', desc: 'Cron only, required. Standard 5-field expression.' },
					{ name: 'timezone', type: 'string', default: 'UTC', desc: 'IANA name. Real timezone handling, so DST does not shift the run.' },
					{ name: 'route', type: 'string', desc: 'Webhook only, read-only. The path segment under `/api/hooks/`.' },
					{ name: 'has_secret', type: 'boolean', desc: 'Read-only. Whether an HMAC signing secret is set.' },
					{ name: 'allow_unsigned', type: 'boolean', default: 'false', desc: 'Accept unauthenticated calls, but only while the trigger has no secret. Every webhook trigger is created with one and rotation always issues another, so today this flag has no effect. A sender that cannot sign uses `X-Nashira-Token` instead.' },
					{ name: 'allow_target_override', type: 'boolean', default: 'false', desc: 'Let the request body choose the targets. With `target_devices` configured it can only **narrow** them; with none configured, the body\'s list is used as sent.' },
					{ name: 'target_devices', type: 'uuid[]', default: '[]', desc: 'Devices this trigger runs against.' },
					{ name: 'input_defaults', type: 'object | null', desc: 'Input merged into every firing.' },
					{ name: 'enabled', type: 'boolean', default: 'true', desc: 'Off suspends firing without deleting the definition.' },
					{ name: 'next_run_at / last_run_at / last_run_status / fire_count', type: 'various', desc: 'Read-only execution telemetry.' },
					{ name: 'last_run_id', type: 'uuid | null', desc: 'Read-only. The run the last firing started.' },
					{ name: 'last_error', type: 'string | null', desc: 'Read-only. Why the last firing failed — or why the scheduler acted on its own: a firing skipped because it was past the catch-up window, or a cron with no future occurrence, which is also disabled.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'A trigger fires as its creator',
				text: 'A cron or webhook run executes as the user who **created** the trigger, with that user\'s current role and permissions — not as whoever edited it last. That identity is also what a step calling Nashira\'s own API (`${secret:session:current:jwt}`) authenticates with. If the creator has been deleted or deactivated, the run is anonymous and such calls fail with 401.'
			},
			{ kind: 'heading', text: 'Calling a webhook trigger' },
			{
				kind: 'code',
				caption: 'Signed request',
				text: `POST /api/hooks/{route}
X-Nashira-Signature: sha256=<hex HMAC-SHA256 of the raw body>
X-Nashira-Delivery: <unique id per delivery, optional but recommended>
Content-Type: application/json

{ "site": "madrid", "probe_count": 3, "target_devices": ["…"] }

→ 202 Accepted
{ "job_id": "…", "status": "queued" }`
			},
			{
				kind: 'list',
				items: [
					'The whole JSON body is the run input; there is no `input` wrapper. It is merged over `input_defaults`, with the body winning per key, so the example above is read as `{{ input.site }}`. `target_devices` is read from the same object and honoured only with `allow_target_override`.',
					'The `sha256=` prefix is optional. A sender that cannot compute an HMAC may send the secret itself in `X-Nashira-Token` instead. Both are compared in constant time.',
					'The body is capped at 256 KB; a larger one gets 413.',
					'An unknown route and a bad signature both return the **same 401**, so routes cannot be enumerated.',
					'`X-Nashira-Delivery` deduplicates: a sender that retries after a timeout gets the original job back instead of a second run.',
					'The response is immediate — the run happens on the job queue, so a slow workflow never times the sender out.'
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'A webhook caller can only narrow the targets',
				text: 'It authenticates with a shared secret, not a user session, so it does not pass the RBAC a manual run does. With `allow_target_override` on, the body may select a subset of the configured devices — never add one. **The exception is a trigger with no configured targets:** the body\'s list is then accepted as sent, and only each device\'s environment flags stand in the way. Configure targets before enabling the override on a production trigger.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/workflows/{workflowId}/triggers', role: 'Viewer', desc: 'List triggers for a workflow.' },
					{ method: 'POST', path: '/api/workflows/{workflowId}/triggers', role: 'Operator', desc: 'Create. A webhook trigger returns its secret **once** — store it now.' },
					{ method: 'PUT', path: '/api/workflows/{workflowId}/triggers/{id}', role: 'Operator', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/workflows/{workflowId}/triggers/{id}', role: 'Operator', desc: 'Soft delete; the trigger is also disabled, so it stops firing at once.' },
					{ method: 'POST', path: '/api/workflows/{workflowId}/triggers/{id}/rotate-secret', role: 'Operator', desc: 'Issue a new signing secret, returned once.' },
					{ method: 'POST', path: '/api/hooks/{route}', role: 'Public', desc: 'The inbound endpoint. Authenticated by HMAC, not by session.' }
				]
			}
		]
	},
	{
		slug: 'acceptance-tests',
		title: 'Acceptance tests',
		group: 'Workflow platform',
		module: 'automation',
		tagline: 'Stored input plus expected assertions — evidence a workflow still does what it claims.',
		uiPath: '/workflows/{id} → Tests',
		apiBase: '/api/workflows/{workflowId}/tests',
		role: 'Viewer',
		purpose:
			'Pin down the behaviour you rely on: given this input and these devices, these assertions must hold. Run before a promotion so "it worked last month" is a fact rather than a memory.',
		keywords: 'test assertion verify qa regression gate',
		blocks: [
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'What this test pins down.' },
					{ name: 'description', type: 'string | null', desc: 'Why it matters.' },
					{ name: 'input', type: 'object', default: '{}', desc: 'Run input for the test execution.' },
					{ name: 'target_devices', type: 'uuid[]', default: '[]', desc: 'Which devices to run against.' },
					{ name: 'assertions', type: 'array', default: '[]', desc: 'What must hold after the run, as `{ kind, path, expected }` objects — see the kinds below.' },
					{ name: 'last_status', type: 'string | null', desc: 'Read-only. `passed`, `failed` or `error` for the most recent execution; null when never run or cleared by an edit.' },
					{ name: 'last_run_id / last_run_at', type: 'various', desc: 'Read-only. The run that produced the verdict, and when.' },
					{ name: 'last_failures', type: 'array', desc: 'Read-only. Which assertions failed, so you do not re-run to find out.' },
					{ name: 'result_is_current', type: 'boolean', desc: 'Read-only. False once the workflow changed after the verdict — a stale pass is not a pass.' }
				]
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'Editing the workflow clears the verdict',
				text: 'A structural edit (nodes or edges) sets `last_status` and `last_failures` back to null on every test of the workflow: a verdict measured on a different graph is not kept as evidence. `result_is_current` is also false whenever the last verdict\'s schema hash differs from the current one.'
			},
			{
				kind: 'values',
				title: 'Assertion kinds',
				rows: [
					{ value: 'status_equals', desc: '`expected` is the run status: `completed` or `failed`.' },
					{ value: 'step_succeeded', desc: '`path` is a node id; the step must have changed something or found nothing to change.' },
					{ value: 'step_failed', desc: '`path` is a node id; the step must have failed.' },
					{ value: 'output_equals', desc: '`path` is `<node_id>.<a.b[0]>`; the value there, as text, must equal `expected`.' },
					{ value: 'output_contains', desc: 'Same path; the value must contain `expected`, ignoring case.' }
				]
			},
			{
				kind: 'prose',
				text: 'An unknown `kind` fails the assertion rather than being skipped, so a typo cannot turn a check into no check. Running a test returns `{ status, run_id, failures, schema_hash }`.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/workflows/{workflowId}/tests', role: 'Viewer', desc: 'List tests with their last verdicts.' },
					{ method: 'POST', path: '/api/workflows/{workflowId}/tests', role: 'Operator', desc: 'Create.' },
					{ method: 'PUT', path: '/api/workflows/{workflowId}/tests/{id}', role: 'Operator', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/workflows/{workflowId}/tests/{id}', role: 'Operator', desc: 'Soft delete.' },
					{ method: 'POST', path: '/api/workflows/{workflowId}/tests/{id}/run', role: 'Operator', desc: 'Execute the test and record the verdict.' }
				]
			}
		]
	},
	{
		slug: 'versions',
		title: 'Versions',
		group: 'Workflow platform',
		module: 'automation',
		tagline: 'Immutable snapshots, written at promotion.',
		uiPath: '/workflows/{id} → Versions',
		apiBase: '/api/workflows/{id}/versions',
		role: 'Viewer',
		purpose:
			'Every promotion freezes the exact graph that was approved. That is what makes "what was running in production on the 3rd?" answerable, and what makes rolling back a known quantity rather than an archaeology exercise.',
		keywords: 'snapshot history rollback restore promotion immutable',
		blocks: [
			{
				kind: 'prose',
				text: 'The snapshot is written **in the same transaction** as the promotion. There is no window in which a workflow is marked production while its snapshot is missing or belongs to a different graph.'
			},
			{
				kind: 'params',
				title: 'Snapshot contents',
				rows: [
					{ name: 'nodes / edges / input_schema / metadata', type: 'various', desc: 'The graph exactly as promoted.' },
					{ name: 'schema_hash', type: 'string', desc: 'Fingerprint, so a restored draft can be compared against what ran.' },
					{ name: 'version', type: 'number', desc: 'Which promotion this was.' },
					{ name: 'environment', type: 'string', desc: 'What it was promoted into.' },
					{ name: 'change_summary', type: 'string', desc: 'The reason recorded at the time.' },
					{ name: 'promoted_by / promoted_at', type: 'string / timestamp', desc: 'Who promoted it (username) and when. The four-eyes approver is not on the snapshot — it is recorded in the promotion\'s audit event.' },
					{ name: 'simulation_id', type: 'uuid | null', desc: 'The simulation the gate accepted. Returned by the list.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Restore produces a draft',
				text: 'Restoring never overwrites the live workflow and never re-enters production directly. You get a new draft, which goes through the ordinary promotion path — so a rollback is reviewed like any other change.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/workflows/{id}/versions', role: 'Viewer', desc: 'Snapshots for a workflow.' },
					{ method: 'GET', path: '/api/workflows/versions/{versionId}', role: 'Viewer', desc: 'One snapshot in full.' },
					{ method: 'POST', path: '/api/workflows/versions/{versionId}/restore', role: 'Operator', desc: 'Create a new draft from it.' }
				]
			}
		]
	},
	{
		slug: 'vendor-commands',
		title: 'Vendor commands',
		group: 'Workflow platform',
		module: 'fleet',
		tagline: 'One intent, the right CLI per platform.',
		uiPath: '/admin/vendor-commands',
		apiBase: '/api/vendor-commands',
		role: 'Viewer',
		purpose:
			'Write "show the BGP summary" once instead of branching on ios / nxos / eos / junos inside every workflow. The catalogue resolves an intent plus a platform into the actual command. In the console it lives under Integrate → Vendor commands.',
		keywords: 'cli multivendor intent platform ios nxos eos junos commands parser',
		blocks: [
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'intent', type: 'string', required: true, desc: 'Platform-independent name — `bgp_summary`, `interface_status`.' },
					{ name: 'platform', type: 'string', required: true, desc: 'Matches `Device.platform`. `(intent, platform)` is the lookup key.' },
					{ name: 'command', type: 'string', required: true, desc: 'The CLI text actually sent.' },
					{ name: 'description', type: 'string | null', desc: 'What it returns.' },
					{ name: 'read_only', type: 'boolean', default: 'true', desc: 'Whether the command changes state. Informational: it tells a reviewer, the engine does not read it.' },
					{ name: 'parser_template', type: 'string | null', desc: 'Optional template turning raw CLI output into structured data for later steps.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/vendor-commands', role: 'Viewer', desc: 'List, filterable by intent or platform.' },
					{ method: 'GET', path: '/api/vendor-commands/resolve', role: 'Viewer', desc: 'Resolve `?intent=…&platform=…`, or `?intent=…&deviceId=…` to use that device\'s platform, to a command. A miss is a 404 — it never falls back to another platform\'s syntax.' },
					{ method: 'GET', path: '/api/vendor-commands/platforms', role: 'Viewer', desc: 'Every known platform with `command_count` and `device_count`, so a platform with devices but no commands stands out.' },
					{ method: 'POST', path: '/api/vendor-commands', role: 'Operator', desc: 'Create. An `(intent, platform)` that already exists is refused with 409 `vendor_command_taken`.' },
					{ method: 'PUT', path: '/api/vendor-commands/{id}', role: 'Operator', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/vendor-commands/{id}', role: 'Operator', desc: 'Soft delete.' }
				]
			},
			{
				kind: 'prose',
				text: 'The catalogue is seeded at start-up from the shipped `Skills/vendors/*.yaml` files — Arista EOS, Cisco ASA/IOS/NX-OS/XE/XR, Fortinet, Huawei, Juniper Junos and others. Seeding only adds missing `(intent, platform)` pairs: an edited command is kept, and one you deleted is not brought back by a redeploy.'
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Marking a command read-only is a claim for reviewers',
				text: 'Today `read_only` is informational: nothing in the engine reads it to decide retries. An `ssh` step is `non_reversible` whatever command it sends, so it is never retried automatically. Label it honestly anyway — it is what a reviewer relies on.'
			}
		]
	}
];
