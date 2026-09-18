import type { DocSection } from '../types';

export const administration: DocSection[] = [
	{
		slug: 'users',
		title: 'Users',
		group: 'Administration',
		module: 'core',
		tagline: 'Accounts, roles and lockout.',
		uiPath: '/admin/users',
		apiBase: '/api/users',
		role: 'Admin',
		purpose: 'Who can sign in, and at what role. Everything else in the console derives from this.',
		keywords: 'accounts roles password lock login identity',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Govern → Users**.' },
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'username', type: 'string', required: true, desc: 'Unique login name.' },
					{ name: 'email', type: 'string', required: true, desc: 'Contact address.' },
					{ name: 'role', type: 'string', default: 'viewer', desc: '`viewer`, `operator` or `admin`. See Roles and access.' },
					{ name: 'password', type: 'string', required: true, desc: '**Write-only.** Must satisfy the password policy (400 `password_policy`). Omit on update to leave the existing password alone.' },
					{ name: 'profile_id', type: 'uuid | null', desc: 'Read-only here. Which assistant profile this user gets in chat — assigned through **Profiles** (`PUT /api/profiles/users/{userId}/profile`).' },
					{ name: 'is_active', type: 'boolean', default: 'true', desc: 'Deactivating blocks sign-in while keeping history attributable.' },
					{ name: 'locked', type: 'boolean', desc: 'Read-only. Set after repeated failed sign-ins — 5 attempts lock the account for 15 minutes by default (`Auth:Lockout`). It expires on its own, and a successful sign-in after that clears the counter; there is no admin unlock.' },
					{ name: 'password_changed_at', type: 'timestamp', desc: 'Read-only. Useful for rotation policies.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'Deactivate rather than delete where you can',
				text: 'Promotions, approvals and audit events reference the user id. A deactivated account keeps that history readable, and its username stays taken — which matters because the four-eyes check on promotion compares `approved_by` with the promoter\'s **username**.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/users', role: 'Admin', desc: 'List.' },
					{ method: 'GET', path: '/api/users/{id}', role: 'Admin', desc: 'One user.' },
					{ method: 'POST', path: '/api/users', role: 'Admin', desc: 'Create. The new user\'s credentials are emailed to them; the response says whether that worked in `credentials_email_sent`, with a `warning` when it did not — hand the password over another way.' },
					{ method: 'PUT', path: '/api/users/{id}', role: 'Admin', desc: 'Update email, role, active state or password. You cannot change your own role (`self_demote`) or deactivate yourself (`self_deactivate`).' },
					{ method: 'DELETE', path: '/api/users/{id}', role: 'Admin', desc: 'Soft delete: the account is deactivated and its history stays attributable. You cannot delete yourself (`self_delete`).' }
				]
			},
			{ kind: 'heading', text: 'The first administrator' },
			{
				kind: 'prose',
				text: 'In **Development**, `DevSeedService` creates `admin` / `admin` on boot, and `POST /api/auth/bootstrap` is available. Change that password at once, from `/account` — see **Roles and access**.'
			},
			{
				kind: 'note',
				tone: 'error',
				title: 'Production has no first-admin path yet',
				text: 'On a **Production** boot against an empty database nothing is seeded, and `POST /api/auth/bootstrap` answers 404. There is currently no supported way to create the first administrator there — treat a Production install as blocked on it.'
			}
		]
	},
	{
		slug: 'permissions',
		title: 'Permissions',
		group: 'Administration',
		module: 'governance',
		tagline: 'Per-user tool domains for the agent, on top of the role.',
		uiPath: '/admin/permissions',
		apiBase: '/api/permissions',
		role: 'Admin',
		purpose:
			'The role decides what the REST API allows. This decides which **tool domains** the agent will use on that person\'s behalf — so an operator can be allowed to run workflows but not to open an SSH session from chat.',
		keywords: 'access domains tools agent granular restrict ssh device integration mcp api spec',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Govern → Tool permissions**.' },
			{
				kind: 'values',
				title: 'What a grant can name',
				rows: [
					{ value: '<domain>', desc: 'Every tool of one capability domain. The domains come from the agent\'s tool matrix: `api`, `audit`, `common`, `credential`, `device`, `email`, `export`, `file`, `git`, `integration`, `inventory`, `knowledge`, `learning`, `loader`, `mcp`, `permission`, `profile`, `provider`, `report`, `secret`, `skill`, `spec`, `user`, `workflow`. SSH execution (`device_connect`) belongs to `device`.' },
					{ value: 'integration:<slug>', desc: 'One registered integration, e.g. `integration:netbox-lab`.' },
					{ value: 'mcp:<name>', desc: 'One registered MCP server, e.g. `mcp:Splunk`.' },
					{ value: 'api:<api>', desc: 'One uploaded API spec, e.g. `api:netbox`.' }
				]
			},
			{
				kind: 'prose',
				text: 'A permission grant is the intersection of two checks, not a replacement: the role still gates the endpoint underneath. Granting write on `integration:netbox-lab` to a viewer does not let them write to NetBox. Each grant carries `can_read`, `can_write` and `can_execute`; a target with no grant follows the role.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/permissions/domains', role: 'Admin', desc: 'Everything a grant can name — domains plus the integrations, MCP servers and specs registered now — as `{ key, kind, label, description }`.' },
					{ method: 'GET', path: '/api/permissions/users/{userId}', role: 'Admin', desc: 'One user\'s grants.' },
					{ method: 'PUT', path: '/api/permissions/users/{userId}', role: 'Admin', desc: 'Body `{ permissions: [{ tool_domain, can_read, can_write, can_execute, inherit }] }`. Upserts each target sent; `inherit: true` removes that grant. Targets not sent are left alone. An unknown target is rejected rather than ignored.' }
				]
			}
		]
	},
	{
		slug: 'profiles',
		title: 'Profiles',
		group: 'Administration',
		module: 'ai-studio',
		tagline: 'Which tone and depth a user gets in chat.',
		uiPath: '/admin/profiles',
		apiBase: '/api/profiles',
		role: 'Viewer',
		purpose:
			'A profile names a response style and the skills it assumes, then attaches to users. On every turn of that user the assistant receives it — plus the free text the user wrote about themselves on the Account page — as a `[USER PROFILE CONTEXT]` system message, and adapts tone, depth and format. It never changes tools, permissions or confirmations. It is how a NOC operator and a network architect get different assistants without running two systems.',
		keywords: 'persona assistant skills response style assignment',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Intelligence → AI Studio → Profiles**.' },
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Internal identifier, unique among active profiles. Fixed after creation.' },
					{ name: 'display_name', type: 'string', desc: 'What users see. Falls back to `name` when omitted.' },
					{ name: 'description', type: 'string | null', desc: 'Who this profile is for.' },
					{ name: 'skills', type: 'string[]', default: '[]', desc: 'Capabilities the assistant may assume of this user, as free strings (e.g. "networking", "python"). Descriptive — not prompt skill names.' },
					{ name: 'response_style', type: 'string | null', desc: 'Tone and format guidance the assistant follows for users on this profile.' },
					{ name: 'display_order', type: 'number', default: '0', desc: 'Ordering in the picker.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/profiles', role: 'Viewer', desc: 'List — a user needs to see their own. The `/admin/profiles` screen itself is admin-only; reading through the API is not.' },
					{ method: 'GET', path: '/api/profiles/{id}', role: 'Viewer', desc: 'One profile.' },
					{ method: 'POST', path: '/api/profiles', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/profiles/{id}', role: 'Admin', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/profiles/{id}', role: 'Admin', desc: 'Soft delete; detaches every user.' },
					{ method: 'GET', path: '/api/profiles/users/me/profile', role: 'Viewer', desc: 'My assignment: profile_id + custom_profile_text.' },
					{ method: 'PUT', path: '/api/profiles/users/me/profile', role: 'Viewer', desc: 'Set my own profile and free text (max 500 chars).' },
					{ method: 'GET', path: '/api/profiles/users/{userId}/profile', role: 'Admin', desc: 'Another user\'s assignment.' },
					{ method: 'PUT', path: '/api/profiles/users/{userId}/profile', role: 'Admin', desc: 'Set another user\'s profile and text (replaces both). Also in Users → Edit.' }
				]
			}
		]
	},
	{
		slug: 'credentials',
		title: 'Credentials',
		group: 'Administration',
		module: 'secrets',
		tagline: 'How Nashira authenticates to devices and systems.',
		uiPath: '/admin/credentials',
		apiBase: '/api/credential',
		role: 'Admin',
		purpose:
			'One record per way of signing in — an SSH login, a git token, an OAuth2 client. Devices reference one as `credential_id`, repositories, integrations and MCP servers as `auth_credential_id`; the material itself is encrypted at rest and never returned. Integrations and MCP servers accept only credentials that can authenticate an HTTP request — `password`, `token`, `api_key` or `oauth2`, never an SSH `key`.',
		keywords: 'ssh key password token api key oauth2 secrets encryption pat',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Operate → Credentials**.' },
			{
				kind: 'values',
				title: 'Auth methods and what each carries',
				rows: [
					{ value: 'password', desc: 'Username + password. SSH login or HTTP basic.' },
					{ value: 'key', desc: 'Private key, optionally with a passphrase. SSH.' },
					{ value: 'token', desc: 'A bearer token or personal access token.' },
					{ value: 'api_key', desc: 'A key, sent in the `token` field, plus the header it travels in — `api_key_header`, defaulting to `X-API-Key`.' },
					{ value: 'oauth2', desc: 'Client credentials grant: `client_id`, client secret, `token_url`, `scopes`. Only the secret is secret — the rest are not, per RFC 6749.' }
				]
			},
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'How it appears in pickers.' },
					{ name: 'type', type: 'string', default: 'ssh', desc: 'Free-form context tag — `ssh`, `git_token`, `netbox`.' },
					{ name: 'auth_method', type: 'string', default: 'password', desc: 'One of the five above; decides which fields apply. A missing or unrecognised value is stored as `password`, without an error. Material is checked on the final state: `key` needs `private_key`, `token` and `api_key` need `token`, `oauth2` needs `client_id`, `client_secret` and `token_url`; `password` may be completed later.' },
					{ name: 'username', type: 'string | null', desc: 'For password and some key setups.' },
					{ name: 'password / private_key / key_passphrase / token / client_secret', type: 'string', desc: '**Write-only.** Responses expose `has_password`, `has_private_key`, `has_token`, `has_client_secret`.' },
					{ name: 'api_key_header, client_id, token_url, scopes', type: 'string | null', desc: 'Non-secret companions, returned normally.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'One-way by construction',
				text: 'No endpoint reads a credential value — not even for an admin. Handlers inject the material server-side (the SSH runner, the git client). If a value is lost, replace it; there is no recovery path, and that is the point.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/credential', role: 'Admin', desc: 'List — metadata and has_* flags only.' },
					{ method: 'GET', path: '/api/credential/{id}', role: 'Admin', desc: 'One credential, metadata only.' },
					{ method: 'POST', path: '/api/credential', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/credential/{id}', role: 'Admin', desc: 'Update. Omitted secret fields keep their stored values.' },
					{ method: 'DELETE', path: '/api/credential/{id}', role: 'Admin', desc: 'Soft delete.' }
				]
			}
		]
	},
	{
		slug: 'secrets',
		title: 'Secrets',
		group: 'Administration',
		module: 'secrets',
		tagline: 'The named secret store integrations point at.',
		uiPath: '/admin/secrets',
		apiBase: '/api/secrets',
		role: 'Admin',
		purpose:
			'Encrypted values addressed by name. An integration, a spec or an inventory source references one as `${secret:secret:<name>:value}` instead of embedding it, so rotating the value in one place updates everything that uses it.',
		keywords: 'vault encrypted settings name reference rotation resolver',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Operate → Secrets**.' },
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'The lookup key: 2–64 chars, lowercase letters/digits/hyphens/underscores, starting and ending with a letter or digit. Referenced as `${secret:secret:<name>:value}`. Immutable, so a reference written once keeps pointing at the same thing.' },
					{ name: 'description', type: 'string | null', desc: 'What this credential authorizes, for whoever inherits it.' },
					{ name: 'value', type: 'string', required: true, desc: '**Write-only.** Encrypted at rest and never returned. Rotate by writing a new one; omit it on update to edit the description alone. An empty value on update is refused (400 `secret_value_empty`) — deleting the secret is the only way to remove it.' },
					{ name: 'has_value', type: 'boolean', desc: 'Read-only. Whether a value is stored — the only thing the API tells you about it.' },
					{ name: 'created_by', type: 'string | null', desc: 'Read-only. Who created the entry.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/secrets', role: 'Admin', desc: 'List with `has_value` flags.' },
					{ method: 'POST', path: '/api/secrets', role: 'Admin', desc: 'Create. 409 `secret_name_taken` if the name is taken — including by a deleted secret.' },
					{ method: 'GET', path: '/api/secrets/{id}', role: 'Admin', desc: 'One entry, metadata only.' },
					{ method: 'PUT', path: '/api/secrets/{id}', role: 'Admin', desc: 'Edit the description, or rotate the value by sending one.' },
					{ method: 'DELETE', path: '/api/secrets/{id}', role: 'Admin', desc: 'Delete; answers 204. Soft: the name stays reserved and audit entries keep resolving.' }
				]
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'The other reference sources',
				text: 'The same resolver reads more than this table. `${secret:credential:<name>:password}` (also `username`, `private_key`, `passphrase`, `token`, `client_secret`) pulls from a stored credential, `${secret:ai_provider:<name>:api_key}` from a provider, and `${secret:integration:<name>:<path.in.auth>}` from an integration’s auth config. A `<name>` can also be the record\'s id. `${secret:session:current:jwt}` is different: it is the calling user\'s own bearer, lent only to calls aimed at this backend (the built-in `na_*` specs use it), so the agent reaches the API with exactly that user\'s rights — a messaging turn or a scheduled run mints a short-lived token for the identity it runs as. It cannot be read from stored configuration. An unresolved reference is left literal on purpose, so it shows up in the logs instead of being sent as an empty credential.'
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Secret or credential?',
				text: 'A **credential** is how to authenticate to something (an SSH key, an OAuth client). A **secret** is a named value other configuration points at — an API token an integration references. When in doubt: if a device or repository will select it from a list, it is a credential.'
			}
		]
	},
	{
		slug: 'learnings',
		title: 'Learnings',
		group: 'Administration',
		module: 'ai-studio',
		tagline: 'Recorded fixes for errors the agent has hit before.',
		uiPath: '/admin/learnings',
		apiBase: '/api/learnings',
		role: 'Admin',
		purpose:
			'When a tool call fails in a recognisable way, a learning supplies the fix instead of letting the agent rediscover it. Self-correction that is inspectable, rather than a model quietly guessing better next time.',
		keywords: 'self correction fixes error pattern confidence retry agent',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Intelligence → AI Studio → Learnings**.' },
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'error_pattern', type: 'string', required: true, desc: 'What the failure looks like — the matcher.' },
					{ name: 'error_category', type: 'string', default: 'unknown', desc: 'Classification of the failure, for grouping and reporting.' },
					{ name: 'category', type: 'string', desc: 'Read-only. `knowledge` for curated entries (everything created here) or `learning` for what the engine discovered.' },
					{ name: 'service_type / tool_name', type: 'string', desc: 'Where it applies. Narrow this so a fix does not leak into unrelated tools.' },
					{ name: 'fix_strategy', type: 'string', default: 'parameter_adjust', desc: '`parameter_adjust` corrects the failed call by merging `fix_params` into its arguments; `escalate` gives the agent `fix_params.message` as a hint to stop retrying and escalate. Any other value is stored as `parameter_adjust`.' },
					{ name: 'fix_params', type: 'object | null', desc: 'What the fix does: the argument delta to merge for `parameter_adjust`, or `{ "message": "…" }` for `escalate`.' },
					{ name: 'confidence', type: 'number', desc: 'Read-only. How much to trust it: starts at 0.8 when created here and is recomputed from the success and failure counts. Below 0.3 a learning is no longer offered.' },
					{ name: 'success_count / failure_count', type: 'number', desc: 'Read-only. Whether the fix actually works.' },
					{ name: 'is_system', type: 'boolean', desc: 'Read-only. Shipped with the product rather than learned here. System entries cannot be edited or deleted (403), and their confidence is never adjusted.' },
					{ name: 'is_active', type: 'boolean', desc: 'Retire a learning without deleting the evidence.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/learnings', role: 'Admin', desc: 'List with hit counts, most trusted first. `?category=` and `?tool=` filter it.' },
					{ method: 'POST', path: '/api/learnings', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/learnings/{id}', role: 'Admin', desc: 'Update. 403 on a system entry.' },
					{ method: 'DELETE', path: '/api/learnings/{id}', role: 'Admin', desc: 'Soft delete. 403 on a system entry.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Watch the failure count',
				text: 'A learning with a rising failure count is worse than none — it confidently applies a fix that no longer works. Retire those rather than tuning the confidence down.'
			}
		]
	},
	{
		slug: 'loader',
		title: 'Loader',
		group: 'Administration',
		module: 'ai-studio',
		tagline: 'Validate skills and specs before they go live.',
		uiPath: '/admin/loader',
		apiBase: '/api/loader',
		role: 'Admin',
		purpose:
			'Community-authored content is the intended path — take, modify, validate. The loader is the validation half: it checks a template functionally and for security patterns. Every save of a skill or spec runs the same checks and records its verdict.',
		keywords: 'validation template security upload hot reload community authoring',
		blocks: [
			{ kind: 'prose', text: 'In the sidebar: **Intelligence → AI Studio → Validation**.' },
			{
				kind: 'params',
				title: 'Validation record',
				rows: [
					{ name: 'kind', type: 'string', desc: 'What was validated — a skill or a spec.' },
					{ name: 'target_name', type: 'string', desc: 'Which one.' },
					{ name: 'ok', type: 'boolean', desc: 'Overall verdict.' },
					{ name: 'issues', type: 'array', desc: 'What was found, each as `{ severity, message }` — `error` or `warning`.' },
					{ name: 'user_id / at', type: 'various', desc: 'Who saved it and when.' }
				]
			},
			{
				kind: 'values',
				title: 'What is checked',
				rows: [
					{ value: 'skill — error', desc: 'Empty content; more than 64 KiB; text that tries to override prior instructions, disregard the system prompt, leak secrets or the system prompt, or bypass confirmations, permissions or governance.' },
					{ value: 'skill — warning', desc: 'Script or exec markers (`<script`, `javascript:`, `eval(`).' },
					{ value: 'spec — error', desc: 'Empty content; more than 512 KiB; not parseable as OpenAPI YAML; more than 500 operations.' },
					{ value: 'spec — warning', desc: 'No operations found; a loopback or cloud-metadata address (`localhost`, `127.0.0.1`, `169.254.169.254`).' }
				]
			},
			{
				kind: 'prose',
				text: 'A verdict is `ok` when no issue is an `error`; a save with an error is refused. Validating here is a dry run: nothing is installed and nothing is recorded. The history is written when a skill or spec is actually saved — through `/api/skills`, `/api/skills/builtin/{name}`, `/api/skills/import` or `/api/ai/specs` — so "was this checked before it went in?" has an answer that does not depend on memory.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'POST', path: '/api/loader/validate', role: 'Admin', desc: 'Dry-run validation. Body `{ kind: "skill" | "spec", name, content }`. Nothing is recorded.' },
					{ method: 'GET', path: '/api/loader/validations', role: 'Admin', desc: 'Verdicts recorded when skills and specs were saved. `?kind=skill|spec` filters them.' }
				]
			}
		]
	}
];
