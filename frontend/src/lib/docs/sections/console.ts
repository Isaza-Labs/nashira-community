import type { DocSection } from '../types';

// The console's own screens: the landing pages and the admin tools that watch and tune
// the platform rather than configure what it talks to.
export const consoleScreens: DocSection[] = [
	{
		slug: 'account',
		title: 'Account',
		group: 'Getting started',
		module: 'core',
		tagline: 'Your password and the persona the assistant answers you with.',
		uiPath: '/account',
		apiBase: '/api/auth',
		role: 'Viewer',
		purpose:
			'The one screen every user owns. Change your password, pick your assistant profile and describe yourself in a line or two, so the agent pitches its answers at the right depth. It sits in the sidebar footer, next to Themes.',
		keywords: 'password change profile persona me self logout sign out',
		blocks: [
			{
				kind: 'params',
				title: 'Changing your password',
				rows: [
					{ name: 'current_password', type: 'string', required: true, desc: 'Proves it is you and not a borrowed session. A wrong value is recorded in the sign-in log.' },
					{ name: 'new_password', type: 'string', required: true, desc: 'At least 12 characters, with an upper-case letter, a lower-case letter, a digit and a symbol by default (`Auth:PasswordPolicy`). It may not contain your username.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'A new password signs you out everywhere',
				text: 'Every refresh token you hold is revoked, so each open session — this browser included — goes back through login. That is the point: if the old password leaked, whoever used it is out too.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'POST', path: '/api/auth/change-password', role: 'Viewer', desc: 'Body `{ current_password, new_password }`. Answers 204.' },
					{ method: 'POST', path: '/api/auth/logout', role: 'Viewer', desc: 'Body `{ refresh_token }`. Revokes that token; the console clears its session either way.' },
					{ method: 'GET', path: '/api/profiles/users/me/profile', role: 'Viewer', desc: 'Your profile and free text.' },
					{ method: 'PUT', path: '/api/profiles/users/me/profile', role: 'Viewer', desc: 'Set them. The free text is capped at 500 characters. See **Profiles**.' }
				]
			}
		]
	},
	{
		slug: 'estate-overview',
		title: 'Overview',
		group: 'Operations',
		module: 'core',
		tagline: 'The estate at a glance — every number links to the list behind it.',
		uiPath: '/overview',
		role: 'Viewer',
		purpose:
			'Where to go when you want the state of things rather than a conversation: how many devices and in what condition, whether inventory is fresh, how much automation and knowledge exists, and — for admins — what the agent has been doing. `/` itself opens Chat; press `g` then `o` to land here.',
		keywords: 'dashboard home landing estate status stats fleet summary',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Overview (the entry at the very top)**.' },
			{
				kind: 'values',
				title: 'The cards',
				rows: [
					{ value: 'Devices', desc: 'Total and by status, the alarming states first. From `GET /api/device/stats`. Fleet module.' },
					{ value: 'Inventory sources', desc: 'How many there are and how many have not synced in the last 24 hours — a source that never synced counts as stale. Fleet module.' },
					{ value: 'Workflows', desc: 'How many exist. Automation module.' },
					{ value: 'Knowledge', desc: 'How many articles exist. Knowledge module.' },
					{ value: 'Recent agent activity', desc: 'The last tool calls the agent made, from the audit trail. Admins only, and only with the governance module.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'A missing card is not a zero',
				text: 'Overview opens in every deployment. A card whose module is off is left out rather than shown as 0, so an install without fleet does not read as a fleet with no devices.'
			}
		]
	},
	{
		slug: 'sessions',
		title: 'Sessions',
		group: 'Governance',
		module: 'observability',
		tagline: 'Every agent conversation in the installation, and what the agent did in it.',
		uiPath: '/admin/sessions',
		apiBase: '/api/sessions',
		role: 'Admin',
		purpose:
			'The oversight view of the agent. The transcript answers "what was said"; the turns answer "what was done" — model, tokens and every tool call with its arguments and result. Turns are recorded as they happen, because the audit trail only records mutations and a turn that read twenty things and changed nothing leaves no trace there.',
		keywords: 'conversations agent oversight transcript turns tool calls tokens chat history admin',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Govern → Sessions**.' },
			{
				kind: 'params',
				title: 'Session',
				rows: [
					{ name: 'conversation_id / user_id / username / title', type: 'various', desc: 'Whose conversation it is.' },
					{ name: 'status', type: 'string', desc: 'The conversation\'s state.' },
					{ name: 'message_count / turn_count / tool_call_count / failed_turn_count', type: 'number', desc: 'How much happened, and how much of it went wrong.' },
					{ name: 'tokens_in / tokens_out', type: 'number', desc: 'Model usage across every turn.' },
					{ name: 'created_at / updated_at', type: 'timestamp', desc: 'The list is ordered by the last update, newest first.' }
				]
			},
			{
				kind: 'params',
				title: 'Turn',
				rows: [
					{ name: 'model', type: 'string', desc: 'The model that answered.' },
					{ name: 'user_message / assistant_text', type: 'string', desc: 'What was asked and what came back.' },
					{ name: 'tool_calls / tool_call_count', type: 'array / number', desc: 'Every tool call with its arguments and result.' },
					{ name: 'tokens_in / tokens_out / iterations', type: 'number', desc: 'Usage, and how many model round-trips the turn took.' },
					{ name: 'status', type: 'string', desc: '`completed`, `awaiting_confirmation`, `error`, `timeout` or `truncated`.' },
					{ name: 'error / started_at / elapsed_ms', type: 'various', desc: 'Why it failed, when it started and how long it took.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/sessions', role: 'Admin', desc: 'Every conversation with its counters. Filter with `userId`; paginate with `limit`/`offset`.' },
					{ method: 'GET', path: '/api/sessions/{id}', role: 'Admin', desc: 'One conversation: its `messages` and its `turns`.' },
					{ method: 'GET', path: '/api/sessions/turns', role: 'Admin', desc: 'Turns across every conversation, newest first. Filter with `userId` and `status` — `status=error` is the quick way to find what is failing.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'This reads everyone\'s chats',
				text: 'A user\'s own history is `/api/ai/conversations`, scoped to them. This view is the organisation-wide one, which is why it is admin-only.'
			}
		]
	},
	{
		slug: 'traces',
		title: 'Traces',
		group: 'Governance',
		module: 'observability',
		tagline: 'What the platform is doing, live — including everything that changed nothing.',
		uiPath: '/admin/traces',
		apiBase: '/api/admin/traces',
		role: 'Admin',
		purpose:
			'The operational trail. The audit trail records what changed and the sign-in log records who signed in; traces record what happened — a job claimed and never finished, a cron that resolved to no devices, a pip install that timed out. Use it as a live tail, or to reconstruct one request end to end by its request id.',
		keywords: 'observability logs trace tail request id forensics latency slow duration worker scheduler',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Govern → Traces**.' },
			{
				kind: 'params',
				title: 'Trace event',
				rows: [
					{ name: 'category', type: 'string', desc: '`http`, `ai`, `tool`, `workflow`, `worker`, `scheduler` or `system`.' },
					{ name: 'action', type: 'string', desc: 'Dotted name of what happened.' },
					{ name: 'status', type: 'string', desc: '`started`, `completed` or `failed`.' },
					{ name: 'duration_ms', type: 'number | null', desc: 'How long it took, when it has finished.' },
					{ name: 'actor / user_id', type: 'various', desc: 'Who fired it — a user, or the identity an unattended run bound itself to.' },
					{ name: 'request_id', type: 'string | null', desc: 'The same id the audit trail stores, so one request can be followed across both.' },
					{ name: 'error_message / metadata_json / at', type: 'various', desc: 'Why it failed, the details (job id, model, device count…) and when.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/admin/traces', role: 'Admin', desc: 'Filters: `search` (a fragment of any text column, metadata included), `category`, `status`, `user` (id, part of a username or email, or an actor), `request_id`, `min_duration_ms`, `from`, `to`. `limit` defaults to 100, max 500. Newest first — except with `min_duration_ms`, which sorts slowest first; `sorted_by` says which.' },
					{ method: 'GET', path: '/api/admin/traces/summary', role: 'Admin', desc: 'Counts by category and status, in-flight and failed events, and the slowest ones, over the last `minutes` (default 60, max a week).' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Two weeks of history',
				text: 'The hourly retention sweep deletes traces older than 14 days. `from` and `to` are both optional, so leaving them out searches everything still kept.'
			}
		]
	},
	{
		slug: 'slos',
		title: 'SLOs',
		group: 'Governance',
		module: 'core',
		tagline: 'What the platform commits to, measured against thresholds you can move.',
		uiPath: '/admin/slo',
		apiBase: '/api/admin/slo',
		role: 'Admin',
		purpose:
			'Five service-level objectives, measured over a window of days. The set is fixed in code; only the thresholds are yours to change, and every change lands in the audit trail.',
		keywords: 'service level objective latency error rate throughput breach reliability',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Govern → SLOs**.' },
			{
				kind: 'values',
				title: 'The objectives (built-in target)',
				rows: [
					{ value: 'run_latency_p95_seconds', desc: 'Run latency, 95th percentile of finish − start. Lower is better; 600 s.' },
					{ value: 'error_rate', desc: 'Runs that ended failed, over all finished runs. Lower is better; 0.05.' },
					{ value: 'throughput_jobs_per_hour', desc: 'Jobs that succeeded per hour. Higher is better; 5. Measured only when something was queued — an idle platform is not a slow one.' },
					{ value: 'promotion_latency_seconds', desc: 'Median time from a workflow\'s creation to a promotion. Lower is better; 86 400 s.' },
					{ value: 'contained_failure_rate', desc: 'Of the failed runs, those that rolled every change back. Higher is better; 0.95.' }
				]
			},
			{
				kind: 'params',
				title: 'Objective',
				rows: [
					{ name: 'key / label / unit', type: 'string', desc: 'Which objective, and how it is shown.' },
					{ name: 'target / default_target', type: 'number', desc: 'The threshold in force, and the built-in one to go back to.' },
					{ name: 'value', type: 'number | null', desc: 'The measurement. Null means the window held nothing to measure — no signal, not a breach.' },
					{ name: 'better / method', type: 'string', desc: '`lower` or `higher`, and how the value is computed.' },
					{ name: 'breach', type: 'boolean', desc: 'Whether the value misses the target.' },
					{ name: 'updated_by', type: 'string | null', desc: 'Who moved the target; null while the built-in one applies.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/admin/slo', role: 'Admin', desc: 'The objectives measured over `days` (default 7, max 90), plus the window start.' },
					{ method: 'PUT', path: '/api/admin/slo/targets/{key}', role: 'Admin', desc: 'Body `{ target }`, a positive number. A ratio above 1 is refused rather than clamped. Unknown key → 404.' },
					{ method: 'DELETE', path: '/api/admin/slo/targets/{key}', role: 'Admin', desc: 'Back to the built-in target.' }
				]
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'A breach is a row, not a page',
				text: 'A daily sweep measures the last week and writes each missed objective to the audit trail as `slo.breach`. Nothing is emailed or paged — whatever notifies your team should read the trail filtered on that action.'
			}
		]
	},
	{
		slug: 'navigation-access',
		title: 'Navigation access',
		group: 'Governance',
		module: 'governance',
		tagline: 'Which areas of the console each role — or each user — is offered.',
		uiPath: '/admin/navigation-permissions',
		apiBase: '/api/navigation-permissions',
		role: 'Admin',
		purpose:
			'Hide areas of the console a role or a person has no use for. A page is shown when its module is on, the user\'s role reaches it, and no navigation setting hides it. This decides what the menu offers — it is not a security boundary: the API still checks the role on every call.',
		keywords: 'menu sidebar visibility hide pages role user navigation permissions',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Govern → Navigation access**.' },
			{
				kind: 'params',
				title: 'Visibility entry',
				rows: [
					{ name: 'page_key', type: 'string', required: true, desc: 'The page\'s path, e.g. `/admin/traces` — no query or fragment. Unique within one request.' },
					{ name: 'visible', type: 'boolean', desc: 'Show or hide it.' },
					{ name: 'inherit', type: 'boolean', desc: 'Write-only. `true` removes this override, so the broader scope decides again.' }
				]
			},
			{
				kind: 'list',
				items: [
					'A user override beats the role override, and a page with neither is visible.',
					'A disabled module hides its pages whatever is stored here.',
					'`/admin/navigation-permissions` itself can never be hidden, so no administrator can be locked out of this screen.'
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/navigation-permissions/me', role: 'Viewer', desc: 'Your effective overrides. The console asks on every load, in every deployment.' },
					{ method: 'GET', path: '/api/navigation-permissions/roles/{role}', role: 'Admin', desc: 'Overrides for `admin`, `operator` or `viewer`.' },
					{ method: 'PUT', path: '/api/navigation-permissions/roles/{role}', role: 'Admin', desc: 'Body `{ permissions: [{ page_key, visible, inherit }] }`. Upserts the entries sent; the before and after are audited.' },
					{ method: 'GET', path: '/api/navigation-permissions/users/{userId}', role: 'Admin', desc: 'One user\'s own overrides.' },
					{ method: 'PUT', path: '/api/navigation-permissions/users/{userId}', role: 'Admin', desc: 'Same body, for one user.' }
				]
			}
		]
	},
	{
		slug: 'admin-dashboard',
		title: 'Admin dashboard',
		group: 'Administration',
		module: 'observability',
		tagline: 'Queue, runs, failures and sign-ins — the health of the platform on one page.',
		uiPath: '/admin',
		apiBase: '/api/admin/metrics',
		role: 'Admin',
		purpose:
			'`/admin` is a dashboard, reached from the user menu: how deep and how old the job queue is, runs per day by status, the workflows failing most, and sign-in successes, failures and lockouts. Choose a 7- or 30-day window; the page refreshes itself every 30 seconds.',
		keywords: 'admin dashboard metrics queue runs failing sign-ins health charts',
		blocks: [
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/admin/metrics/runs', role: 'Admin', desc: 'Runs per day, `final_states`, `by_environment` and `top_failing`, over `days` (default 7, max 90).' },
					{ method: 'GET', path: '/api/admin/metrics/auth', role: 'Admin', desc: 'Sign-ins per day plus `successes`, `failures` and `lockouts`, over `days`.' },
					{ method: 'GET', path: '/api/admin/metrics/queue', role: 'Admin', desc: 'Jobs by status, `queued`, `claimed`, and the age of the oldest queued job.' },
					{ method: 'GET', path: '/api/admin/metrics/devices', role: 'Admin', desc: 'Devices by status, vendor and site, synced versus typed in, and how many allow each environment.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Quiet days are still days',
				text: 'Every daily series carries one bucket per day in the window, zero-run days included, so a chart never reads a quiet week as continuous activity. The buckets are UTC days.'
			}
		]
	},
	{
		slug: 'settings',
		title: 'Settings',
		group: 'Administration',
		module: 'core',
		tagline: 'Platform behaviour you can change without a redeploy.',
		uiPath: '/admin/settings',
		apiBase: '/api/admin/settings',
		role: 'Admin',
		purpose:
			'A short list of values the code reads at the moment it uses them, so a change takes effect without a restart — on this replica at once, on the others within 30 seconds. Every change is audited. A setting whose module is off is neither shown nor writable.',
		keywords: 'configuration runtime toggle ssh destructive pip timeout git file size',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Govern → Settings**.' },
			{
				kind: 'values',
				title: 'The settings (default)',
				rows: [
					{ value: 'Ssh:AllowDestructiveCommands', desc: '`false`. When off, commands that erase or reload a device are refused before they reach it, whatever the workflow says. Fleet module.' },
					{ value: 'Python:PackageProvisioningEnabled', desc: '`true`. Whether approved pip packages are installed; turning it off leaves them pending rather than failing them. Automation module.' },
					{ value: 'Python:PipInstallTimeoutSeconds', desc: '`300`. How long one install may take; values below 30 count as 30. Automation module.' },
					{ value: 'Git:MaxFileBytes', desc: '`5242880` (5 MB). Files above this are not read into memory by the git browser. Git module.' }
				]
			},
			{
				kind: 'params',
				title: 'Setting',
				rows: [
					{ name: 'key / category / display_name / description / input_type', type: 'string', desc: 'What it is and how the screen renders it. `input_type` is `bool` or `int`.' },
					{ name: 'effective', type: 'string', desc: 'The value in use now.' },
					{ name: 'stored_value', type: 'string | null', desc: 'What was set on this screen; null when nobody has.' },
					{ name: 'source', type: 'string', desc: '`stored`, `configuration` or `default` — where `effective` came from. Only `stored` is yours to change here.' },
					{ name: 'default_value', type: 'string', desc: 'The built-in value.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/admin/settings', role: 'Admin', desc: 'The settings this deployment offers, plus `refresh_seconds`.' },
					{ method: 'PUT', path: '/api/admin/settings/{key}', role: 'Admin', desc: 'Body `{ value }` as a string: `true`/`false` for a toggle, a non-negative integer for a number. Anything else is 400.' },
					{ method: 'DELETE', path: '/api/admin/settings/{key}', role: 'Admin', desc: 'Clear the stored value, falling back to configuration or the default; the response says which applies now.' }
				]
			}
		]
	}
];
