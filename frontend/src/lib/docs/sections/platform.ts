import type { DocSection } from '../types';

// Cross-cutting concepts. Everything else in the docs assumes these.
export const platform: DocSection[] = [
	{
		slug: 'quick-start',
		title: 'Quick start',
		group: 'Getting started',
		module: 'core',
		tagline: 'From signing in to reading your first run.',
		uiPath: '/chat',
		purpose:
			'The shortest path that ends with a workflow that actually ran. Every step links to the section that explains it properly — read this once, then come back only for the parts you skipped.',
		keywords: 'quickstart first steps tutorial onboarding begin new user walkthrough hello world',
		blocks: [
			{
				kind: 'note',
				tone: 'primary',
				title: 'Fifteen minutes, assuming someone has set the console up',
				text: 'Steps 1–3 need the **operator** role and step 0 needs an **admin**. If you are the admin doing this from scratch, do step 0 first; if someone handed you an account, start at step 1.'
			},
			{ kind: 'heading', text: '0. What an admin sets up once' },
			{
				kind: 'list',
				items: [
					'**A user account** for you, with a role — `/admin/users`. See **Users**.',
					'**An AI provider**, or chat has no model to talk to — `/admin/providers`. See **AI providers**.',
					'**A credential** the devices can be reached with — `/admin/credentials`. See **Credentials**.',
					'Optionally an **inventory source**, so devices arrive by sync instead of by typing — `/inventory`.'
				]
			},
			{ kind: 'heading', text: '1. Sign in' },
			{
				kind: 'prose',
				text: 'Everything except `/login` needs a session. Opening any other URL while signed out sends you to `/login?redirect=…` and returns you to where you were aiming once you are in — so a link someone pasted you still lands correctly.'
			},
			{
				kind: 'prose',
				text: '`/` is not a dashboard: it redirects to **Chat**. The agent is the front door, and `/overview` is where you go when you want the estate at a glance instead.'
			},
			{ kind: 'heading', text: '2. Give it something to act on' },
			{
				kind: 'steps',
				items: [
					'Open `/devices` and add one — **name** and **ip_address** are the only required fields; set **platform** too or vendor commands cannot resolve. Attach the credential from step 0.',
					'Leave the three `allow_*` flags alone for now. They default to true, which is what lets a draft workflow touch this device at all.',
					'If you have a NetBox or similar, add an **inventory source** instead and sync — it fills the same table and keeps it current.'
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'One device is enough',
				text: 'Pools, sites and roles are how you avoid maintaining target lists later. None of it is needed to get a first run out.'
			},
			{ kind: 'heading', text: '3. Get a workflow' },
			{
				kind: 'prose',
				text: 'There is no graph editor in the console — a workflow arrives one of two ways, and both land it in `draft`:'
			},
			{
				kind: 'list',
				items: [
					'**Ask the agent.** In `/chat`, describe what you want done. It drafts the workflow, and you read it before anything executes.',
					'**Import one.** `/workflows` → **Import** takes a YAML artifact exported from this instance, or a portable **bundle** — including one authored in Flow Weaver.'
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'An import is always a new draft',
				text: 'The `id` and `environment` inside the file are ignored. A file cannot overwrite a local workflow, and it cannot arrive pre-marked production and skip the gate.'
			},
			{ kind: 'heading', text: '4. Read the plan, then run it' },
			{
				kind: 'steps',
				items: [
					'Open the workflow. The **Plan** tab shows the materialised execution order and whether the whole thing is reversible — that banner is the thing to read before you click anything.',
					'**Run workflow** opens the confirmation dialog: the runtime inputs (from the workflow\'s `input_schema`, or inferred from the `{{ input.* }}` references when it declares none) and the target devices.',
					'Submit. A refused device is named rather than skipped, so a run either targets everything you picked or it does not start.'
				]
			},
			{ kind: 'heading', text: '5. Read the run' },
			{
				kind: 'prose',
				text: '`/runs/{id}` paints the step headers first — result, timing, error code — and fetches a step\'s resolved input, output and logs when you open its card. `/runs` is the same evidence across every workflow, filtered server-side.'
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'You are still in draft, and that is the point',
				text: 'Nothing you just did touched qa or production. Those are reached only by **promotion**, which is gated — and a device can refuse a draft outright. Iterate here until the run is boring, then promote.'
			},
			{ kind: 'heading', text: 'Getting around while you do it' },
			{
				kind: 'list',
				items: [
					'`Ctrl`/`⌘` + `K` — the command palette: every destination plus the create actions.',
					'`g` then a letter — jump without opening anything: **c** chat, **o** overview, **d** devices, **w** workflows, **s** snippets, **i** integrations, **k** knowledge, **r** reports, **h** docs.',
					'`?new=1` on any listing opens its create form directly.'
				]
			},
			{
				kind: 'values',
				title: 'The screens this walkthrough touches',
				rows: [
					{ value: '/chat', desc: 'The agent. Also where `/` sends you.' },
					{ value: '/overview', desc: 'Devices by status, inventory freshness, recent agent activity.' },
					{ value: '/devices', desc: 'The inventory a run targets.' },
					{ value: '/admin/credentials', desc: 'How devices are reached. Admin.' },
					{ value: '/workflows', desc: 'Drafts, and the Import action.' },
					{ value: '/runs', desc: 'What happened, step by step.' },
					{ value: '/docs', desc: 'These pages.' }
				]
			},
			{
				kind: 'note',
				tone: 'error',
				title: 'If chat answers with nothing',
				text: 'No **AI provider** is configured, or your profile has no active model. That is an admin fix at `/admin/providers` — not something a retry will resolve.'
			},
			{ kind: 'heading', text: 'Where to go next' },
			{
				kind: 'list',
				items: [
					'**How Nashira fits together** — the five layers, and why the agent drafts but never executes.',
					'**Finding things** — the six groups, the tabbed surfaces, and the keyboard.',
					'**Roles and access** — what your role actually permits, and why the UI hiding a button is not the control.',
					'**Environments and promotion** — how a draft becomes production.',
					'**Workflows** and **Snippets** — the shape of what you just ran.'
				]
			}
		]
	},
	{
		slug: 'overview',
		title: 'How Nashira fits together',
		group: 'Getting started',
		module: 'core',
		tagline: 'The five layers, and which one you should be in.',
		purpose:
			'Nashira is a network operations console with an AI agent in front of it. Read this first: almost every "where do I do X?" question is answered by knowing which of the five layers X belongs to.',
		keywords: 'architecture concepts start here introduction model',
		blocks: [
			{ kind: 'heading', text: 'The five layers' },
			{
				kind: 'list',
				items: [
					'**Inventory** — what exists. Devices, device pools, inventory sources, credentials.',
					'**Capabilities** — what can be done to it. Snippets, vendor commands, integrations, MCP servers, API specs.',
					'**Automation** — how it gets done, repeatably. Workflows, triggers, acceptance tests, versions, runs.',
					'**Governance** — who may do it and when. Policies, promotion gates, the audit trail.',
					'**Interfaces** — how a human asks. Chat with the agent, the console screens, reports and exports.'
				]
			},
			{
				kind: 'prose',
				text: 'A request usually travels down that list. You ask the agent to do something (interface); it drafts or runs a workflow (automation) built from snippets (capabilities) against devices (inventory); policies decide whether it is allowed (governance) and the audit trail records what happened.'
			},
			{ kind: 'heading', text: 'The rule that shapes everything' },
			{
				kind: 'note',
				tone: 'primary',
				title: 'The AI builds, but does not execute',
				text: 'The language model composes and explains; the deterministic executor performs. A workflow run is a typed, reproducible artifact — not a transcript of what a model decided mid-sentence. This is why mutations live in workflows rather than in tool calls the model improvises.'
			},
			{
				kind: 'prose',
				text: 'Practically: reads and low-risk idempotent operations happen on the fly, while bulk or irreversible changes are materialised as a workflow, confirmed, and then executed by the engine.'
			},
			{ kind: 'heading', text: 'Where to go next' },
			{
				kind: 'list',
				items: [
					'New to the console → **Roles and access**, then **Devices**.',
					'Automating something → **Workflows**, then **Snippets**.',
					'Setting up guardrails → **Policies**, then **Environments and promotion**.',
					'Connecting an external system → **Integrations** or **MCP servers**.'
				]
			}
		]
	},
	{
		slug: 'navigation',
		title: 'Finding things',
		group: 'Getting started',
		module: 'core',
		tagline: 'Six groups, three tabbed surfaces, and a keyboard that beats all of them.',
		purpose:
			'The console is grouped by what you are trying to do, not by which table a record lives in. Knowing the six groups is enough to find anything without hunting.',
		keywords: 'navigation sidebar menu command palette shortcuts groups tabs search where is',
		blocks: [
			{
				kind: 'values',
				title: 'The groups',
				rows: [
					{ value: 'Intelligence', desc: 'The agent itself: chat, and the AI Studio that decides what it knows.' },
					{ value: 'Build', desc: 'What you author: workflows, snippets, git.' },
					{ value: 'Integrate', desc: 'Systems outside Nashira: connections and vendor commands.' },
					{ value: 'Operate', desc: 'The day to day: runs, schedules, devices, inventory sources, device pools, credentials, secrets, artifacts.' },
					{ value: 'Govern', desc: 'Policies, tool permissions, the Python allowlist, SLOs, the audit trail, and user accounts.' },
					{ value: 'Help', desc: 'Reference: knowledge and these docs.' }
				]
			},
			{ kind: 'heading', text: 'Tabbed surfaces' },
			{
				kind: 'prose',
				text: 'Three groups of pages are edited together often enough that they share a tab bar, so moving between them is one click rather than a trip through an index: **AI Studio** (skills, API specs, providers, learnings, profiles, validation), **Connections** (integrations, MCP, messaging, email) and **Artifacts** (reports, exports).'
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'A spec and a skill are two halves of one thing',
				text: 'A spec says what an integration can be asked to do; a skill says how to operate it. They sit next to each other on the AI Studio tab bar for that reason — and an integration links straight to its own specs and skills.'
			},
			{ kind: 'heading', text: 'Keyboard' },
			{
				kind: 'list',
				items: [
					'`Ctrl`/`⌘` + `K` opens the command palette: every destination, plus create actions that open the right form directly.',
					'`g` then a letter jumps without opening anything — d devices, w workflows, s snippets, i integrations, k knowledge, r reports, o overview, c chat, h docs.',
					'Any listing can be reached with `?new=1` to open its create form — that is what the palette actions link to.'
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Admin is a lock, not a floor',
				text: 'Needing the admin role hides a page from everyone else, but it does not bury it: everything an admin can reach has a direct sidebar entry. The index at `/admin` is a map of the same groups, not a separate place things live.'
			}
		]
	},
	{
		slug: 'roles',
		title: 'Roles and access',
		group: 'Getting started',
		module: 'core',
		tagline: 'Three roles, cumulative, enforced by the API rather than the UI.',
		purpose:
			'Every endpoint carries a minimum role. The console hides what you cannot use, but the check that matters happens server-side — a hidden button is a courtesy, not a control.',
		keywords: 'rbac permissions security admin operator viewer authorization',
		blocks: [
			{
				kind: 'values',
				title: 'The roles',
				rows: [
					{
						value: 'viewer',
						desc: 'Any authenticated user. Reads devices, workflows, runs, reports, knowledge. Cannot change anything.'
					},
					{
						value: 'operator',
						desc: 'Viewer plus the ability to act: create, run and promote workflows, manage devices and pools, author snippets, store reports.'
					},
					{
						value: 'admin',
						desc: 'Operator plus configuration and secrets: users, permissions, credentials, integrations, MCP servers, policies, AI providers, prompt skills, API specs, email and messaging channels, the audit trail.'
					}
				]
			},
			{
				kind: 'prose',
				text: 'The roles are cumulative: `Operator` accepts admin and operator, `Viewer` accepts any authenticated user. There is no admin-only *read* tier, so admin-only reads (the audit trail, the loader) are expressed as admin endpoints.'
			},
			{ kind: 'heading', text: 'Per-user tool permissions' },
			{
				kind: 'prose',
				text: 'Roles gate the REST API. The **agent** has a second, finer gate: per-user tool-domain permissions, managed under Agent → Tool permissions. A user can hold the operator role and still be denied the `ssh` tool domain in chat.'
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'Secrets never travel outward',
				text: 'No endpoint returns a stored secret value — not a password, private key, token, or API key. Responses carry booleans instead (`has_password`, `has_credentials`, `has_value`). Writing is one-way: send a new value to replace one, or a `clear_*` flag to remove it. Sending nothing leaves the stored value untouched.'
			},
			{
				kind: 'endpoints',
				title: 'Session',
				rows: [
					{ method: 'POST', path: '/api/auth/login', role: 'Public', desc: 'Exchange credentials for an access + refresh token pair.' },
					{ method: 'POST', path: '/api/auth/refresh', role: 'Public', desc: 'Rotate the refresh token. Reusing a rotated token revokes the whole chain.' },
					{ method: 'GET', path: '/api/auth/me', role: 'Viewer', desc: 'The current session: username, role, profile.' }
				]
			}
		]
	},
	{
		slug: 'environments',
		title: 'Environments and promotion',
		group: 'Getting started',
		module: 'automation',
		tagline: 'draft → qa → production, with the device allow-trio deciding what each can touch.',
		purpose:
			'Environments keep an unfinished workflow away from production hardware. A workflow carries an environment; a device declares which environments may target it; a run is refused when those disagree.',
		keywords: 'draft qa production promote promotion gate lifecycle stages',
		blocks: [
			{
				kind: 'values',
				title: 'Environments',
				rows: [
					{ value: 'draft', desc: 'Being written. Every new and imported workflow starts here.' },
					{ value: 'qa', desc: 'Under test. Promoted from draft once it validates.' },
					{ value: 'production', desc: 'Live. Promotion into it can require a second approver and can be gated by policy.' }
				]
			},
			{ kind: 'heading', text: 'The device allow-trio' },
			{
				kind: 'prose',
				text: 'Each device carries `allow_draft`, `allow_qa` and `allow_production`. A run resolves its targets once, up front, and **refuses by name** when a device does not permit the workflow\'s environment.'
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'Refusing beats filtering',
				text: 'The engine never silently drops a disallowed device. A run that quietly executed on 3 of 5 devices would report success having done 60% of the work — and nobody would know which 60%. So the whole run is refused, naming the device that blocked it.'
			},
			{ kind: 'heading', text: 'Promotion' },
			{
				kind: 'steps',
				items: [
					'Built-in gate: the workflow must validate and, for qa → production, be approved by someone other than its author (four-eyes).',
					'Policy gate: any `gate` policy matching this transition is evaluated — for example "three successful qa runs in the last seven days".',
					'On success an immutable **workflow version** snapshot is written in the same transaction as the promotion, so the artifact and the approval can never disagree.'
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'POST', path: '/api/workflows/{id}/promote', role: 'Operator', desc: 'Advance one environment. Returns 412 `approval_required` when four-eyes applies, or the unmet gate requirements.' }
				]
			}
		]
	},
	{
		slug: 'audit',
		title: 'Audit trail',
		group: 'Getting started',
		module: 'governance',
		tagline: 'A hash-chained record of every mutation, verifiable end to end.',
		uiPath: '/admin/audit',
		apiBase: '/api/audit',
		role: 'Admin',
		purpose:
			'Answers "who changed this, when, and what did it look like before?" — and proves the answer has not been edited afterwards. Every event carries the hash of its predecessor, so removing or altering one breaks the chain from that point on.',
		keywords: 'trail hash chain tamper evident compliance history who changed',
		blocks: [
			{
				kind: 'params',
				title: 'Event fields',
				rows: [
					{ name: 'sequence', type: 'number', desc: 'Monotonic position in the chain.' },
					{ name: 'at', type: 'timestamp', desc: 'When the mutation was committed (UTC).' },
					{ name: 'user_id', type: 'uuid | null', desc: 'Who did it. Null for system-initiated events such as a scheduled run.' },
					{ name: 'entity_type', type: 'string', desc: 'The kind of record touched — `Device`, `Workflow`, `Policy`, …' },
					{ name: 'entity_id', type: 'uuid | null', desc: 'Which record.' },
					{ name: 'action', type: 'string', desc: 'create · update · delete · and domain verbs such as promote.' },
					{ name: 'before / after', type: 'object | null', desc: 'The record either side of the change. Secret-bearing fields are omitted.' },
					{ name: 'ip, user_agent, request_id', type: 'string | null', desc: 'Request provenance, for correlating with proxy and application logs.' },
					{ name: 'hash, prev_hash', type: 'string', desc: 'The chain. `hash` covers the event contents and `prev_hash`.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/audit', role: 'Admin', desc: 'List events. Filter by entity type, entity id, user, action and date range.' },
					{ method: 'GET', path: '/api/audit/{id}', role: 'Admin', desc: 'One event including before/after payloads.' },
					{ method: 'POST', path: '/api/audit/verify', role: 'Admin', desc: 'Walk the chain and report the first sequence where the hashes stop agreeing.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'What is deliberately not audited',
				text: 'Read endpoints and diagnostics (a connectivity check, a test message, a policy dry-run) carry `[SkipAudit]`. Auditing them would bury the mutations in noise. Workflow runs are also skipped at the controller because the run itself emits richer per-node audit events.'
			}
		]
	},
	{
		slug: 'jobs',
		title: 'Jobs and scheduling',
		group: 'Getting started',
		module: 'automation',
		tagline: 'The background queue behind webhooks, crons and retention.',
		apiBase: '—',
		purpose:
			'Work that must not run inside a web request goes on a queue. It is why a webhook answers instantly, why a long run does not delay every other cron, and why the same trigger firing on two replicas still produces one run.',
		keywords: 'queue worker background async scheduler cron retention lease skip locked',
		blocks: [
			{ kind: 'heading', text: 'How a job is claimed' },
			{
				kind: 'prose',
				text: 'A worker claims a job with a single `UPDATE … RETURNING` using `FOR UPDATE SKIP LOCKED`. Two workers — or two replicas — never take the same row, because the claim and the read are one statement.'
			},
			{
				kind: 'params',
				title: 'Job fields',
				rows: [
					{ name: 'status', type: 'string', desc: '`queued` → `claimed` → `succeeded` | `failed`.' },
					{ name: 'type', type: 'string', desc: 'Today only `workflow_run`.' },
					{ name: 'payload', type: 'object', desc: 'Workflow id, input, resolved targets, originating trigger.' },
					{ name: 'attempts', type: 'number', desc: 'Incremented by the claim itself, so a crash still counts.' },
					{ name: 'lease_expires_at', type: 'timestamp', desc: '30 minutes. A job whose lease expires is marked failed, never re-queued.' },
					{ name: 'delivery_key', type: 'string | null', desc: 'Deduplication key from the `X-Nashira-Delivery` header, unique per trigger.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'An expired lease fails, it does not retry',
				text: 'A workflow run is not idempotent in general. Blindly repeating one after a worker died is worse than asking a human to look, so the job is marked failed and left for inspection.'
			},
			{ kind: 'heading', text: 'The scheduler' },
			{
				kind: 'prose',
				text: 'Every tick, the scheduler re-bases each due cron trigger with a conditional update — "set `next_run_at` forward **only if** it still equals what I read". Exactly one replica wins that race and enqueues the job. Because it enqueues rather than executes, one slow workflow no longer delays the rest.'
			},
			{ kind: 'heading', text: 'Retention' },
			{
				kind: 'prose',
				text: 'An hourly sweeper deletes expired report artifacts and finished jobs older than 30 days.'
			},
			{
				kind: 'params',
				title: 'Configuration (appsettings)',
				rows: [
					{ name: 'Jobs:MaxConcurrent', type: 'number', default: '3', desc: 'How many jobs one process runs at a time.' }
				]
			}
		]
	}
];
