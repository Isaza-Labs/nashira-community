import type { DocSection } from '../types';

export const workflows: DocSection[] = [
	{
		slug: 'workflows',
		title: 'Workflows',
		group: 'Workflow platform',
		module: 'automation',
		tagline: 'A directed graph of snippets, validated, versioned and executed deterministically.',
		uiPath: '/workflows',
		apiBase: '/api/workflows',
		role: 'Viewer',
		purpose:
			'The unit of automation. A workflow says what to do, in what order, on which devices, and under what conditions — as data, so it can be reviewed, diffed, tested, promoted and replayed. The engine executes it; no language model is in the execution path.',
		keywords:
			'automation dag graph nodes edges yaml import export run promote simulate bundle portable interchange flow weaver subflow',
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
					{ name: 'metadata', type: 'object | null', desc: 'Free-form authoring metadata (canvas positions, labels).' },
					{ name: 'change_summary', type: 'string', desc: 'Why this edit was made. Carried into the version snapshot on promotion.' },
					{ name: 'conversation_id', type: 'uuid | null', desc: 'The chat this workflow came out of, when the agent drafted it.' },
					{ name: 'environment', type: 'string', default: 'draft', desc: 'Read-only here — changed only by promotion.' },
					{ name: 'version', type: 'number', desc: 'Read-only. Incremented on promotion.' },
					{ name: 'schema_hash', type: 'string', desc: 'Read-only. Canonical fingerprint of nodes + edges; two workflows with the same hash are the same graph.' },
					{ name: 'last_simulation_id', type: 'uuid | null', desc: 'Read-only. Cleared by any structural edit, because the old result no longer describes this graph.' }
				]
			},
			{ kind: 'heading', text: 'Node shape' },
			{
				kind: 'code',
				caption: 'One node',
				text: `{
  "id": "check-reachable",          // unique within the workflow
  "snippet_id": "8f0c…",            // which Snippet runs here
  "type": "task",                   // default "task"
  "config_overrides": {             // inputs for this snippet, templated
    "host": "{{ device.ip_address }}",
    "count": "{{ input.probe_count }}"
  }
}`
			},
			{ kind: 'heading', text: 'Edge shape' },
			{
				kind: 'code',
				caption: 'Three edge types',
				text: `{ "source": "a", "target": "b", "type": "success" }
{ "source": "a", "target": "rollback", "type": "failure" }
{ "source": "a", "target": "c", "type": "conditional",
  "condition": "steps.a.output.changed == true && input.apply == true" }`
			},
			{
				kind: 'values',
				title: 'Edge types',
				rows: [
					{ value: 'success', desc: 'Follow when the source step succeeded. The default when `type` is absent.' },
					{ value: 'failure', desc: 'Follow when it failed — the branch for cleanup or rollback.' },
					{ value: 'conditional', desc: 'Follow when `condition` evaluates true. An unresolvable or malformed condition is false, never true.' }
				]
			},
			{ kind: 'heading', text: 'Templates' },
			{
				kind: 'prose',
				text: 'Any string in `config_overrides` may reference earlier state: `{{ input.x }}` (the run input), `{{ device.ip_address }}` (the current target in a per-device step), and `{{ steps.<node_id>.output.<path> }}` (an earlier step\'s output, with `a.b[0].c` paths).'
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
					{ method: 'GET', path: '/api/workflows', role: 'Viewer', desc: 'List. Filter with `environment`.' },
					{ method: 'GET', path: '/api/workflows/{id}', role: 'Viewer', desc: 'Full workflow including nodes and edges.' },
					{ method: 'POST', path: '/api/workflows', role: 'Operator', desc: 'Create. Validates against workflow.v1 and requires an acyclic graph.' },
					{ method: 'PUT', path: '/api/workflows/{id}', role: 'Operator', desc: 'Update a draft. A structural edit clears the stale simulation link.' },
					{ method: 'DELETE', path: '/api/workflows/{id}', role: 'Operator', desc: 'Soft delete.' },
					{ method: 'GET', path: '/api/workflows/{id}/yaml', role: 'Viewer', desc: 'Compile to YAML. `?download=true` returns it as a file.' },
					{ method: 'GET', path: '/api/workflows/{id}/bundle', role: 'Viewer', desc: 'Export the portable bundle (v3 JSON). `?download=false` returns it inline.' },
					{ method: 'POST', path: '/api/workflows/import', role: 'Operator', desc: 'Import a YAML artifact or a bundle — the file says which by its `kind`.' }
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
				text: 'The YAML export names this instance\'s snippets, integrations and repositories by GUID. On a second instance none of those ids exist, so the import is refused by the same reference check that guards every write — which is why a YAML file only ever comes home. The **bundle** is the portable form: the same nodes and edges, plus everything needed to make their references mean something on the other side. Nashira and Flow Weaver read and write the same format (`workflow-v1-conformance/bundle/SPEC.md`, schema v3), so a workflow authored in either runs in the other.'
			},
			{
				kind: 'params',
				title: 'What travels in a bundle',
				rows: [
					{ name: 'requires', type: 'object', desc: 'What the receiving instance must support: `snippet_types`, `capabilities` (a closed vocabulary — `subflow`, `template_filters`, `run_namespace`, `per_device_scope`, `max_parallel`, `per_pool`, `conditional_edges`, `python_network`, `triggers`) and the `${secret:…}` references the workflow resolves at run time. Checked before anything is created.' },
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
					{ value: 'refused', desc: 'Nothing is created, and the error names every problem at once: `bundle_dependencies_missing`, `bundle_capability_unsupported`, `bundle_reference_untranslatable`, `bundle_subflow_cycle`, `bundle_incomplete`, `bundle_version_unsupported`. Nothing is guessed and nothing is stubbed — a placeholder that returns nothing would turn a shared workflow into a run that goes green having done no work.' },
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
					{ method: 'GET', path: '/api/workflows/runs/{runId}', role: 'Viewer', desc: 'One run with its per-step results.' },
					{ method: 'POST', path: '/api/workflows/{id}/promote', role: 'Operator', desc: 'Advance one environment.' },
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
					{ name: 'status', type: 'string', desc: 'Overall outcome of the execution.' },
					{ name: 'final_state', type: 'string', desc: 'The terminal state the engine settled in.' },
					{ name: 'environment', type: 'string', desc: 'The environment the workflow was in when it ran.' },
					{ name: 'node_count / changed_count / failed_count', type: 'number', desc: 'Steps executed, steps that changed something, steps that failed.' },
					{ name: 'input', type: 'object | null', desc: 'The input the run was given.' },
					{ name: 'target_devices', type: 'uuid[]', desc: 'The resolved targets — after pool expansion, before nothing: the list is never silently narrowed.' },
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
					{ name: 'result', type: 'string', desc: 'Outcome of this step.' },
					{ name: 'error_code', type: 'string | null', desc: 'Machine-readable failure reason.' },
					{ name: 'retryable', type: 'boolean', desc: 'Whether the failure was transient in the handler\'s judgement.' }
				]
			},
			{
				kind: 'endpoints',
				title: 'Reading runs',
				rows: [
					{ method: 'GET', path: '/api/runs', role: 'Viewer', desc: 'Every run across every workflow. Filters (status, environment, final_state, workflow_id, q) are applied server-side, so the total describes the same set as the rows.' },
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
			{ kind: 'heading', text: 'Per-device fan-out' },
			{
				kind: 'prose',
				text: 'A snippet with `target_mode: per_device` runs once per target and aggregates into `{ per_device, total, failed, devices[] }`. The aggregate reports "changed" if any device changed; when handlers say nothing either way, the snippet\'s idempotency tier decides — but an explicit "nothing changed" from every device is respected.'
			}
		]
	},
	{
		slug: 'snippets',
		title: 'Snippets',
		group: 'Workflow platform',
		module: 'automation',
		tagline: 'The seven kinds of step a workflow node can be.',
		uiPath: '/admin/snippets',
		apiBase: '/api/snippets',
		role: 'Viewer',
		purpose:
			'A snippet is one reusable unit of work — ping a host, call a REST endpoint, run a command over SSH, transform data. Workflows compose snippets; nothing else is executable.',
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
					{ value: 'python_snippet', desc: 'Run Python in a restricted sandbox. Admin-gated network access, module allowlist.' },
					{ value: 'git', desc: 'Read, write, commit, pull or push a registered repository from inside a run — so a workflow that needs Git does not have to come back to a person.' },
					{ value: 'report', desc: 'Render markdown into a stored report (markdown/html/pdf), linked to the run in Reports. The output carries the file as base64, ready to attach downstream.' },
					{ value: 'email_send', desc: 'Send mail through a named email channel or the default relay — cc/bcc, HTML and base64 attachments. Non-reversible.' },
					{ value: 'email_mailbox', desc: 'Operate on the inbound (IMAP) side of an email channel: list, read, mark read/unread, move, archive or delete messages. A list step\'s `uids` output feeds the acting steps.' },
					{ value: 'slack_message', desc: 'Post text through a messaging channel (Slack, Teams or webhook); the delivery is recorded against the run. Non-reversible.' },
					{ value: 'ansible_playbook', desc: 'Run the snippet’s YAML playbook against an inventory device (environment-gated) or a literal host. Linux worker with ansible-playbook only.' },
					{ value: 'netconf / snmp_v3', desc: 'Reserved stubs: the types exist so shared workflows validate, and fail with not_implemented until a client library lands.' }
				]
			},
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Unique; also generates the slug.' },
					{ name: 'type', type: 'string', required: true, desc: 'One of the seven above. Decides which handler executes it.' },
					{ name: 'description', type: 'string | null', desc: 'What the step does, for whoever reads the workflow later.' },
					{ name: 'code', type: 'string | null', desc: 'The body — a command for `ssh`, an expression for `transform`, a script for `python_snippet`.' },
					{ name: 'script_language', type: 'string | null', desc: 'Language hint for the editor.' },
					{ name: 'input_schema', type: 'JSON string | null', desc: 'What `config_overrides` should provide. Drives the authoring form.' },
					{ name: 'output_schema', type: 'JSON string | null', desc: 'What the step returns, so later steps can template against it.' },
					{ name: 'target_mode', type: 'string', default: 'once', desc: '`once` runs a single time; `per_device` fans out across the run targets.' },
					{ name: 'timeout_seconds', type: 'number', desc: 'Per-execution timeout.' },
					{ name: 'idempotency', type: 'string | null', desc: 'Author\'s declaration. Null inherits the handler\'s floor.' },
					{ name: 'effective_idempotency', type: 'string', desc: 'Read-only. What the engine will actually use — see the note.' },
					{ name: 'network_enabled', type: 'boolean', default: 'false', desc: '`python_snippet` only. Enabling is an Admin act; disabling any operator can do.' },
					{ name: 'verified', type: 'boolean', desc: 'Marks a snippet as reviewed and trusted for general use.' },
					{ name: 'logic_diagram_mermaid', type: 'string | null', desc: 'Optional Mermaid diagram documenting the internal logic.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'Idempotency has a floor, not a free choice',
				text: 'Each handler declares the least-safe tier it can honestly claim. An author may declare a step *less* idempotent than that floor, never more — so nobody can mark an SSH configuration change "idempotent" and quietly enable retries on it. `effective_idempotency` is what the engine uses.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/snippets/types', role: 'Viewer', desc: 'The type catalogue with each handler\'s idempotency floor.' },
					{ method: 'GET', path: '/api/snippets', role: 'Viewer', desc: 'List.' },
					{ method: 'GET', path: '/api/snippets/{id}', role: 'Viewer', desc: 'One snippet with its schemas.' },
					{ method: 'POST', path: '/api/snippets', role: 'Operator', desc: 'Create.' },
					{ method: 'PUT', path: '/api/snippets/{id}', role: 'Operator', desc: 'Update. Turning `network_enabled` on requires Admin.' },
					{ method: 'DELETE', path: '/api/snippets/{id}', role: 'Operator', desc: 'Soft delete.' }
				]
			},
			{ kind: 'heading', text: 'The Python sandbox' },
			{
				kind: 'list',
				items: [
					'Modules are an **allowlist**, managed under Build → Python modules. Nothing network-capable is seeded.',
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
					{ name: 'type', type: 'string', required: true, desc: '`cron` or `webhook`.' },
					{ name: 'cron_expression', type: 'string', desc: 'Cron only, required. Standard 5-field expression.' },
					{ name: 'timezone', type: 'string', default: 'UTC', desc: 'IANA name. Real timezone handling, so DST does not shift the run.' },
					{ name: 'route', type: 'string', desc: 'Webhook only, read-only. The path segment under `/api/hooks/`.' },
					{ name: 'has_secret', type: 'boolean', desc: 'Read-only. Whether an HMAC signing secret is set.' },
					{ name: 'allow_unsigned', type: 'boolean', default: 'false', desc: 'Accept unsigned webhook calls. Only for a sender that genuinely cannot sign.' },
					{ name: 'allow_target_override', type: 'boolean', default: 'false', desc: 'Let the request body change the target list — and only to **narrow** it.' },
					{ name: 'target_devices', type: 'uuid[]', default: '[]', desc: 'Devices this trigger runs against.' },
					{ name: 'input_defaults', type: 'object | null', desc: 'Input merged into every firing.' },
					{ name: 'enabled', type: 'boolean', default: 'true', desc: 'Off suspends firing without deleting the definition.' },
					{ name: 'next_run_at / last_run_at / last_run_status / last_error / fire_count', type: 'various', desc: 'Read-only execution telemetry.' }
				]
			},
			{ kind: 'heading', text: 'Calling a webhook trigger' },
			{
				kind: 'code',
				caption: 'Signed request',
				text: `POST /api/hooks/{route}
X-Nashira-Signature: sha256=<hex HMAC-SHA256 of the raw body>
X-Nashira-Delivery: <unique id per delivery, optional but recommended>
Content-Type: application/json

{ "input": { … }, "target_devices": ["…"] }

→ 202 Accepted
{ "job_id": "…", "status": "queued" }`
			},
			{
				kind: 'list',
				items: [
					'The signature is compared in constant time.',
					'An unknown route and a bad signature both return the **same 401**, so routes cannot be enumerated.',
					'`X-Nashira-Delivery` deduplicates: a sender that retries after a timeout gets the original job back instead of a second run.',
					'The response is immediate — the run happens on the job queue, so a slow workflow never times the sender out.'
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'A webhook caller can only narrow the targets',
				text: 'It authenticates with a shared secret, not a user session, so it does not pass the RBAC a manual run does. With `allow_target_override` on, the body may select a subset of the configured devices — never add one.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/workflows/{workflowId}/triggers', role: 'Viewer', desc: 'List triggers for a workflow.' },
					{ method: 'POST', path: '/api/workflows/{workflowId}/triggers', role: 'Operator', desc: 'Create. A webhook trigger returns its secret **once** — store it now.' },
					{ method: 'PUT', path: '/api/workflows/{workflowId}/triggers/{id}', role: 'Operator', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/workflows/{workflowId}/triggers/{id}', role: 'Operator', desc: 'Delete.' },
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
					{ name: 'assertions', type: 'array', default: '[]', desc: 'What must hold after the run — step results, output values, change counts.' },
					{ name: 'last_status', type: 'string | null', desc: 'Read-only. Verdict of the most recent execution.' },
					{ name: 'last_failures', type: 'array', desc: 'Read-only. Which assertions failed, so you do not re-run to find out.' },
					{ name: 'result_is_current', type: 'boolean', desc: 'Read-only. False once the workflow changed after the verdict — a stale pass is not a pass.' }
				]
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'Editing the workflow invalidates the verdict',
				text: '`result_is_current` flips to false when the graph changes. The old result is kept — it is still evidence about the old graph — but it stops counting as current.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/workflows/{workflowId}/tests', role: 'Viewer', desc: 'List tests with their last verdicts.' },
					{ method: 'POST', path: '/api/workflows/{workflowId}/tests', role: 'Operator', desc: 'Create.' },
					{ method: 'PUT', path: '/api/workflows/{workflowId}/tests/{id}', role: 'Operator', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/workflows/{workflowId}/tests/{id}', role: 'Operator', desc: 'Delete.' },
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
					{ name: 'approved_by', type: 'string | null', desc: 'Who signed it off.' }
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
			'Write "show the BGP summary" once instead of branching on ios / nxos / eos / junos inside every workflow. The catalogue resolves an intent plus a platform into the actual command.',
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
					{ name: 'read_only', type: 'boolean', default: 'true', desc: 'Whether the command changes state. Drives idempotency and review expectations.' },
					{ name: 'parser_template', type: 'string | null', desc: 'Optional template turning raw CLI output into structured data for later steps.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/vendor-commands', role: 'Viewer', desc: 'List, filterable by intent or platform.' },
					{ method: 'GET', path: '/api/vendor-commands/resolve', role: 'Viewer', desc: 'Resolve `?intent=…&platform=…` to a command.' },
					{ method: 'POST', path: '/api/vendor-commands', role: 'Operator', desc: 'Create.' },
					{ method: 'PUT', path: '/api/vendor-commands/{id}', role: 'Operator', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/vendor-commands/{id}', role: 'Operator', desc: 'Delete.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Marking a command read-only is a claim about the device',
				text: 'It is used to reason about whether a step can be retried. A write command mislabelled read-only invites the engine to repeat it.'
			}
		]
	}
];
