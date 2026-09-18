import type { DocSection } from '../types';

// Cross-cutting concepts. Everything else in the docs assumes these.
export const platform: DocSection[] = [
	{
		slug: 'quick-start',
		title: 'Quick start',
		group: 'Getting started',
		module: 'core',
		tagline: 'From signing in to your first answer — and your first governed run.',
		uiPath: '/chat',
		purpose:
			'The shortest path from a first question to a workflow that actually ran. Every step links to the section that explains it properly — read this once, then come back only for the parts you skipped.',
		keywords: 'quickstart first steps tutorial onboarding begin new user walkthrough hello world',
		blocks: [
			{
				kind: 'note',
				tone: 'primary',
				title: 'Fifteen minutes, assuming someone has set the console up',
				text: 'Adding devices and running workflows (steps 2, 4 and 5) need the **operator** role, and step 0 needs an **admin**. If you are the admin doing this from scratch, do step 0 first; if someone handed you an account, start at step 1.'
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
					'Leave the `allow_*` flags at their defaults for now: `allow_draft` and `allow_production` start on, `allow_qa` starts off. `allow_draft` is what lets a draft workflow touch this device at all — turn `allow_qa` on before you promote anything that targets it.',
					'If an admin registered an **inventory source** in step 0, sync it instead — it fills the same table and keeps it current. Syncing is an operator act; creating the source is not.'
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'One device is enough',
				text: 'Pools, sites and roles are how you avoid maintaining target lists later. None of it is needed to get a first run out.'
			},
			{ kind: 'heading', text: '3. Ask the agent' },
			{
				kind: 'prose',
				text: 'Open `/chat` and ask about what you just added — "which devices have no platform set?", "show version on <device>". Reads run straight away; a single change is proposed and runs once you confirm it. Most day-to-day work ends here, without a workflow.'
			},
			{ kind: 'heading', text: '4. Get a workflow' },
			{
				kind: 'prose',
				text: 'There is no graph editor in the console — a workflow arrives one of two ways, and both land it in `draft`:'
			},
			{
				kind: 'list',
				items: [
					'**Ask the agent.** In `/chat`, describe what you want done. It drafts the workflow, and you read it before anything executes.',
					'**Import one.** `/workflows` → **Import** takes a YAML artifact exported from this instance, or a portable **bundle** — including one authored in FlowWeaver.'
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'An import is always a new draft',
				text: 'The `id` and `environment` inside the file are ignored. A file cannot overwrite a local workflow, and it cannot arrive pre-marked production and skip the gate.'
			},
			{ kind: 'heading', text: '5. Read the plan, then run it' },
			{
				kind: 'steps',
				items: [
					'Open the workflow. The **Plan** tab shows the materialised execution order and whether the whole thing is reversible — that banner is the thing to read before you click anything.',
					'**Run workflow** opens the confirmation dialog: the runtime inputs (from the workflow\'s `input_schema`, or inferred from the `{{ input.* }}` references when it declares none) and the target devices.',
					'Submit. A refused device is named rather than skipped, so a run either targets everything you picked or it does not start.'
				]
			},
			{ kind: 'heading', text: '6. Read the run' },
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
					'`?new=1` on a listing that has a create action opens its form directly — the same listings the palette offers under its create actions.'
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
				text: 'No **AI provider** is enabled, or the conversation is pinned to a provider an admin has since disabled — the chat says which. Pick another model, or have an admin enable one at `/admin/providers`; a retry alone will not fix it.'
			},
			{ kind: 'heading', text: 'Where to go next' },
			{
				kind: 'list',
				items: [
					'**How Nashira fits together** — the five layers, the autonomy tiers, and when the agent acts in the conversation versus drafting a workflow.',
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
			'Nashira is a self-managed conversational operations layer over the systems you already run — inventory, service management, IPAM, automation platforms and network devices — with network operations as its most extensively validated domain today. Read this first: almost every "where do I do X?" question is answered by knowing which of the five layers X belongs to.',
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
					'**Interfaces** — how a human asks. Chat with the agent (in the console, or from Slack, Teams, WhatsApp and Telegram), the console screens, notifications and email, reports and exports.'
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
				title: 'Ad-hoc when it is safe, an artifact when it is not',
				text: 'Day-to-day questions and single changes are resolved in the conversation, under confirmation. A change that spans many devices or systems, or cannot be undone, is materialised as a workflow you read and approve before the deterministic executor runs it — a typed, reproducible artifact, not a transcript of what a model decided mid-sentence.'
			},
			{
				kind: 'prose',
				text: 'Practically: reads and low-risk idempotent operations happen on the fly, while bulk or irreversible changes are materialised as a workflow, confirmed, and then executed by the engine.'
			},
			{
				kind: 'values',
				title: 'Autonomy tiers',
				rows: [
					{ value: 'autonomous', desc: 'Runs without asking — reads, lookups and simulations.' },
					{ value: 'single_confirm', desc: 'Asks once before running. An approval holds for the rest of the conversation.' },
					{ value: 'elevated_confirm', desc: 'Asks with a heightened confirmation — destructive operations.' },
					{ value: 'human_only', desc: 'Never run by the agent. Also what a tool missing from the matrix falls back to, so an unclassified tool is refused.' }
				]
			},
			{
				kind: 'prose',
				text: 'Every agent tool has a fixed entry in the permission matrix: its domain, the role it needs and its tier. A call to an API spec with `GET`/`HEAD`, or to an MCP tool its server marks read-only on a server with `trust_tool_hints`, needs no confirmation. Every call that is not autonomous spends one of 20 mutation slots per turn. The tiers are not configurable per deployment or per user today — what an admin configures is **tool permissions** and **policies**.'
			},
			{ kind: 'heading', text: 'Modules' },
			{
				kind: 'prose',
				text: 'A deployment runs a chosen set of **modules** — `core` (always on), `chat`, `ai-studio`, `integrations`, `automation`, `fleet`, `governance`, `communications`, `secrets`, `knowledge`, `git`, `artifacts` and `observability`. The **Module** shown on each docs section is the one it needs. With `NASHIRA_MODULES` unset every module runs; set it to restrict the deployment. A disabled module has no pages, no workers and no docs section, and its endpoints answer 503 `module_disabled`; its data is kept, so enabling it again brings it back. `GET /api/modules` reports what this deployment is actually running.'
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'Dependencies are listed, not pulled in',
				text: '`chat` needs `ai-studio`, which needs `integrations`; `communications` needs `chat`; the rest need only `core`. `NASHIRA_MODULES` must name every dependency itself — a list with one missing is refused at startup, naming it.'
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
			'The console is grouped by what you are trying to do, not by which table a record lives in. Knowing the six groups is enough to find anything without hunting. **Overview** sits alone above them; **Account** and **Themes** live in the sidebar footer.',
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
					{ value: 'Govern', desc: 'Policies, tool permissions, navigation access, the Python allowlist, SLOs, the audit trail, traces, agent sessions, user accounts and runtime settings.' },
					{ value: 'Help', desc: 'Reference: knowledge and these docs.' }
				]
			},
			{ kind: 'heading', text: 'Tabbed surfaces' },
			{
				kind: 'prose',
				text: 'Three groups of pages are edited together often enough that they share a tab bar, so moving between them is one click rather than a trip through an index: **AI Studio** (skills, API specs, providers, learnings, profiles, validation), **Integrations** (integrations, MCP servers, notifications, messaging channels, email channels) and **Artifacts** (reports, exports).'
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
					'A listing with a create action opens its form when reached with `?new=1` — that is what the palette actions link to. That covers devices, pools, workflows (the Import dialog), snippets, knowledge, reports, credentials, secrets, users, policies, integrations, MCP servers, AI providers, prompt skills, API specs, Python modules, vendor commands and the notification, messaging and email channels; other listings ignore it.'
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Admin is a lock, not a floor',
				text: 'Needing the admin role hides a page from everyone else, but it does not bury it: everything an admin can reach has a direct sidebar entry. `/admin` is not an index but the **Admin dashboard** — queue, run activity, failures and sign-ins — reached from the user menu.'
			},
			{ kind: 'heading', text: 'Navigation access' },
			{
				kind: 'prose',
				text: 'What the sidebar shows is the intersection of three things: the modules this deployment runs, your role, and **navigation access** — per-role and per-user choices an admin makes under Govern → Navigation access (`/admin/navigation-permissions`). A hidden page is a tidier menu, not a control: the API still decides by role. The navigation access screen itself can never be hidden, so no admin is locked out of undoing it.'
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
						desc: 'Operator plus configuration and secrets: users, tool permissions, navigation access, credentials, secrets, integrations, MCP servers, policies, AI providers, prompt skills, API specs, notification, messaging and email channels, inventory sources, git repositories and their webhooks, the Python allowlist, SLOs, runtime settings, agent sessions, traces and the audit trail.'
					}
				]
			},
			{
				kind: 'prose',
				text: 'The roles are cumulative: `Operator` accepts admin and operator, `Viewer` accepts any authenticated user. There is no admin-only *read* tier, so admin-only reads (the audit trail, the loader) are expressed as admin endpoints. One deployment is one organisation: there are no tenants, so every user works on the same records, separated only by role, tool permissions and navigation access.'
			},
			{ kind: 'heading', text: 'Per-user tool permissions' },
			{
				kind: 'prose',
				text: 'Roles gate the REST API. The **agent** has a second, finer gate: per-user tool-domain permissions, managed under Govern → Tool permissions. A user can hold the operator role and still be denied the `device` tool domain — the one SSH execution belongs to — in chat.'
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
					{ method: 'POST', path: '/api/auth/logout', role: 'Viewer', desc: 'Revoke the refresh token in the body (`refresh_token`).' },
					{ method: 'POST', path: '/api/auth/change-password', role: 'Viewer', desc: 'Body `{ current_password, new_password }`. The **Account** page (`/account`) uses it.' },
					{ method: 'GET', path: '/api/auth/me', role: 'Viewer', desc: 'The current session: `user_id`, `username`, `email`, `role` and `password_changed_at`.' },
					{ method: 'GET', path: '/api/auth/events', role: 'Admin', desc: 'The sign-in log — sign-ins, failures, lockouts, revocations. Filters `userId`, `event`, `from`, `to`, `includeUnattributed`.' },
					{ method: 'POST', path: '/api/auth/bootstrap', role: 'Public', desc: '**Development only**: creates an `admin` account with a generated password and returns it with a token pair, so the API can be exercised. Answers 404 in every other environment.' },
				]
			},
			{ kind: 'heading', text: 'Lockout and password policy' },
			{
				kind: 'list',
				items: [
					'**Lockout** — 5 failed sign-ins lock the account for 15 minutes (`Auth:Lockout:MaxFailedAttempts`, `Auth:Lockout:LockoutMinutes`). The lock expires on its own.',
					'**Password policy** — at least 12 characters with an uppercase letter, a lowercase letter, a digit and a symbol, and not containing the username (`Auth:PasswordPolicy`). A password that fails it is refused with the rule it broke.'
				]
			},
			{ kind: 'heading', text: 'Rate limits' },
			{
				kind: 'values',
				rows: [
					{ value: 'auth_login', desc: '15 per minute per client IP — login, refresh and bootstrap. Complements the per-account lockout.' },
					{ value: 'auth_generic', desc: '100 per minute per user — logout, change-password, me.' },
					{ value: 'read_heavy', desc: '300 per minute per user — list and detail reads.' },
					{ value: 'write_normal', desc: '60 per minute per user — creates, updates and deletes.' },
					{ value: 'ai_chat', desc: '30 per hour per user — every turn reaches a model.' },
					{ value: 'workflow_run', desc: '20 per hour per user — every run does real work.' },
					{ value: 'webhooks', desc: 'Inbound trigger and git webhooks: 60 per minute per (IP, route). Messaging webhooks: 220 per minute per (IP, channel).' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'A 429 says how long to wait',
				text: 'A limited request gets 429 with `Retry-After: 60` and `{ "error": "rate_limited", "retry_after_seconds": 60 }`. Per-IP limits use the real client address, which needs `Network__TrustedProxies` to name the reverse proxy in front of the API — otherwise every caller looks like the proxy and shares one budget.'
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
					{ value: 'qa', desc: 'Under test. Promoted from draft once a simulation of the current graph has passed.' },
					{ value: 'production', desc: 'Live. Promotion into it can require a second approver and can be gated by policy.' }
				]
			},
			{ kind: 'heading', text: 'The device allow-trio' },
			{
				kind: 'prose',
				text: 'Each device carries `allow_draft`, `allow_qa` and `allow_production`. A run resolves its targets once, up front, and **refuses by name** when a device does not permit the workflow\'s environment. A new device allows draft and production but **not qa** — enable `allow_qa` before promoting a workflow that targets it, or its first qa run is refused.'
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
					'Built-in gate: draft → qa needs a successful simulation of the current graph (412 `simulation_missing`, `simulation_failed` or `simulation_stale` otherwise); qa → production needs `approved_by` naming someone other than the person promoting (four-eyes, 412 `approval_required`).',
					'Policy gate: any `gate` policy matching this transition is evaluated — for example "three successful qa runs in the last seven days".',
					'On success an immutable **workflow version** snapshot is written in the same transaction as the promotion, so the artifact and the approval can never disagree.'
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'POST', path: '/api/workflows/{id}/promote', role: 'Operator', desc: 'Advance one environment. Body `{ target, approved_by, change_summary }`. A refusal is 412 with `simulation_missing`, `simulation_failed`, `simulation_stale`, `approval_required` or `policy_blocked` (naming the unmet gate requirements).' }
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
					{ name: 'username', type: 'string | null', desc: 'Read-only. The user id resolved for reading.' },
					{ name: 'actor', type: 'string | null', desc: 'Who acted, as text: the username, or the automation identity (`scheduler`, `workflow-runner`, …) when there is no user. Covered by the hash.' },
					{ name: 'entity_type', type: 'string', desc: 'The kind of record touched, in lower snake case — `device`, `workflow`, `policy`, `device_pool`, `inventory_source`, `mcp_server`, …' },
					{ name: 'entity_id', type: 'uuid | null', desc: 'Which record.' },
					{ name: 'action', type: 'string', desc: 'create · update · delete · and domain verbs such as promote.' },
					{ name: 'before / after', type: 'object | null', desc: 'The change, with secret-bearing fields omitted. Controllers that audit themselves record the record either side; for everything else the generic filter stores `before: null` and the endpoint\'s response as `after`.' },
					{ name: 'ip, user_agent, request_id', type: 'string | null', desc: 'Request provenance, for correlating with proxy and application logs.' },
					{ name: 'hash, prev_hash', type: 'string', desc: 'The chain. `hash` covers the event contents and `prev_hash`.' },
					{ name: 'hash_version', type: 'number', desc: 'Read-only. Which canonical form the hash was taken over, so a row written before a chain change can be told apart.' },
					{ name: 'restorable', type: 'boolean', desc: 'Read-only, detail only. True for a delete event whose record the restore endpoint can bring back.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/audit', role: 'Admin', desc: 'List events. Filters `entityType` (or a comma-separated `entityTypes`), `entityId`, `action` or `actionPrefix`, `userId`, `actor`, `requestId`, `from`, `to`, plus `limit`/`offset`.' },
					{ method: 'GET', path: '/api/audit/{id}', role: 'Admin', desc: 'One event including before/after payloads and `restorable`.' },
					{ method: 'POST', path: '/api/audit/{id}/restore', role: 'Admin', desc: 'Bring back the soft-deleted record a `delete` event points at, and append a `restore` event. Refused for any other action or a type that cannot be restored.' },
					{ method: 'GET', path: '/api/audit/verify', role: 'Admin', desc: 'Walk the chain. Returns `{ valid, count, broken_at_sequence, reason }` — the first sequence where the hashes stop agreeing.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'What is deliberately not audited',
				text: 'Read endpoints and diagnostics (a connectivity check, a test message, a policy dry-run) carry `[SkipAudit]`. Auditing them would bury the mutations in noise. Workflow runs are also skipped at the controller because the run itself emits richer per-node audit events.'
			},
			{ kind: 'heading', text: 'The sign-in log' },
			{
				kind: 'prose',
				text: '`/admin/audit` also shows a second record: sign-ins, failures, lockouts and revocations, from `GET /api/auth/events`. It is **not** part of the hash chain — it is written by unauthenticated callers, so it is pruned instead: rows older than `Auth:AuthEventRetentionDays` (180 by default, 0 keeps them forever) are deleted by the hourly sweeper. The old `/audit` URL redirects here.'
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
					{ name: 'type', type: 'string', desc: '`workflow_run`, or the messaging turn types `agent_message` and `messaging_send`. Each worker claims only its own types.' },
					{ name: 'payload', type: 'object', desc: 'For a `workflow_run`: workflow id, input, resolved targets, originating trigger.' },
					{ name: 'attempts', type: 'number', desc: 'Incremented by the claim itself, so a crash still counts.' },
					{ name: 'lease_expires_at', type: 'timestamp', desc: 'A `workflow_run` holds a 30-minute lease; when it expires the job is marked failed, never re-queued. A messaging job holds 10 minutes and **is** re-queued, because its inbound event makes a repeat safe.' },
					{ name: 'delivery_key', type: 'string | null', desc: 'Deduplication key, unique per trigger: the `X-Nashira-Delivery` header on a trigger webhook; `X-GitHub-Delivery` or `X-Nashira-Delivery` on a git webhook.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'An expired workflow lease fails, it does not retry',
				text: 'A workflow run is not idempotent in general. Blindly repeating one after a worker died is worse than asking a human to look, so the job is marked failed and left for inspection.'
			},
			{ kind: 'heading', text: 'The scheduler' },
			{
				kind: 'prose',
				text: 'Every tick, the scheduler re-bases each due cron trigger with a conditional update — "set `next_run_at` forward **only if** it still equals what I read". Exactly one replica wins that race and enqueues the job. Because it enqueues rather than executes, one slow workflow no longer delays the rest. The scheduler ticks every 30 seconds. A firing more than 10 minutes late — the platform was down — is skipped rather than replayed, recorded in the traces, and the trigger re-bases to its next occurrence. Workers poll the queue every 3 seconds.'
			},
			{ kind: 'heading', text: 'Seeing what is scheduled' },
			{
				kind: 'prose',
				text: '**Schedules** (`/schedules`, under Operate) lists every cron and webhook trigger across all workflows, with its next and last run, and flags an enabled trigger as overdue once its next run is more than five minutes past. It reads `GET /api/schedules` (Viewer), which filters by `type`, `enabled`, `q` and `due_within_hours`.'
			},
			{ kind: 'heading', text: 'Other background services' },
			{
				kind: 'values',
				title: 'Which run depends on the modules enabled',
				rows: [
					{ value: 'core', desc: 'Trace writer (drains the trace queue), settings refresh (keeps runtime settings in step across replicas), SLO breach watcher (a daily sweep that records a missed objective in the audit trail), retention sweeper.' },
					{ value: 'automation', desc: 'Scheduler and job worker, plus the Python package provisioner that installs approved pip modules.' },
					{ value: 'communications', desc: 'Messaging worker and messaging retention, plus Slack Socket Mode and the Teams relay, which dial out instead of waiting on a webhook.' }
				]
			},
			{ kind: 'heading', text: 'Retention' },
			{
				kind: 'prose',
				text: 'An hourly sweeper deletes expired reports, finished jobs older than 30 days, git webhook deliveries older than 30 days, traces older than 14 days, and sign-in events older than `Auth:AuthEventRetentionDays` (180). Tables of a disabled module are left alone. Messaging has its own sweeper for its inbound events, deliveries and spent link tokens.'
			},
			{
				kind: 'params',
				title: 'Configuration (appsettings)',
				rows: [
					{ name: 'Jobs:MaxConcurrent', type: 'number', default: '3', desc: 'How many jobs one process runs at a time, clamped to 1–16. Not in `appsettings.json` — set it through configuration or the environment (`Jobs__MaxConcurrent`).' }
				]
			}
		]
	}
];
