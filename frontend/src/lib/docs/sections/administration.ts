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
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'username', type: 'string', required: true, desc: 'Unique login name.' },
					{ name: 'email', type: 'string', required: true, desc: 'Contact address.' },
					{ name: 'role', type: 'string', required: true, desc: '`viewer`, `operator` or `admin`. See Roles and access.' },
					{ name: 'password', type: 'string', desc: '**Write-only.** Omit on update to leave the existing password alone.' },
					{ name: 'profile_id', type: 'uuid | null', desc: 'Which assistant profile this user gets in chat.' },
					{ name: 'is_active', type: 'boolean', default: 'true', desc: 'Deactivating blocks sign-in while keeping history attributable.' },
					{ name: 'locked', type: 'boolean', desc: 'Read-only. Set by failed sign-in attempts; cleared by an admin.' },
					{ name: 'password_changed_at', type: 'timestamp', desc: 'Read-only. Useful for rotation policies.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'Deactivate rather than delete where you can',
				text: 'Promotions, approvals and audit events reference the user id. A deactivated account keeps that history readable; the four-eyes check on promotion compares user ids, and a missing author cannot be verified against.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/users', role: 'Admin', desc: 'List.' },
					{ method: 'POST', path: '/api/users', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/users/{id}', role: 'Admin', desc: 'Update, including role and lock state.' },
					{ method: 'DELETE', path: '/api/users/{id}', role: 'Admin', desc: 'Delete.' }
				]
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
		keywords: 'access domains tools agent granular restrict ssh netbox',
		blocks: [
			{
				kind: 'values',
				title: 'Domains',
				rows: [
					{ value: 'device', desc: 'Device inventory and diagnostics — ping, connect, query.' },
					{ value: 'netbox', desc: 'NetBox inventory operations.' },
					{ value: 'servicenow', desc: 'ServiceNow ITSM operations.' },
					{ value: 'infoblox', desc: 'Infoblox DDI operations.' },
					{ value: 'awx', desc: 'AWX / Ansible Tower operations.' },
					{ value: 'common', desc: 'General-purpose tools: knowledge, exports, file parsing.' },
					{ value: 'dynamic', desc: 'Calls driven by uploaded API specs.' }
				]
			},
			{
				kind: 'prose',
				text: 'A permission grant is the intersection of two checks, not a replacement: the role still gates the endpoint underneath. Granting `netbox` to a viewer does not let them write to NetBox.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/permissions/domains', role: 'Admin', desc: 'The recognised domain list.' },
					{ method: 'GET', path: '/api/permissions/{userId}', role: 'Admin', desc: 'One user\'s grants.' },
					{ method: 'PUT', path: '/api/permissions/{userId}', role: 'Admin', desc: 'Replace them. Unknown domains are rejected rather than ignored.' }
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
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Internal identifier.' },
					{ name: 'display_name', type: 'string', required: true, desc: 'What users see.' },
					{ name: 'description', type: 'string | null', desc: 'Who this profile is for.' },
					{ name: 'skills', type: 'string[]', default: '[]', desc: 'Capabilities the assistant may assume of this user, as free strings (e.g. "networking", "python"). Descriptive — not prompt skill names.' },
					{ name: 'response_style', type: 'string | null', desc: 'Tone and format guidance the assistant follows for users on this profile.' },
					{ name: 'display_order', type: 'number', desc: 'Ordering in the picker.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/profiles', role: 'Viewer', desc: 'List — a user needs to see their own.' },
					{ method: 'POST', path: '/api/profiles', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/profiles/{id}', role: 'Admin', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/profiles/{id}', role: 'Admin', desc: 'Delete (soft; detaches every user).' },
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
			'One record per way of signing in — an SSH login, a git token, an OAuth2 client. Devices, repositories and integrations reference a credential by id; the material itself is encrypted at rest and never returned.',
		keywords: 'ssh key password token api key oauth2 secrets encryption pat',
		blocks: [
			{
				kind: 'values',
				title: 'Auth methods and what each carries',
				rows: [
					{ value: 'password', desc: 'Username + password. SSH login or HTTP basic.' },
					{ value: 'key', desc: 'Private key, optionally with a passphrase. SSH.' },
					{ value: 'token', desc: 'A bearer token or personal access token.' },
					{ value: 'api_key', desc: 'A key plus the header it travels in — `api_key_header`, defaulting to Authorization.' },
					{ value: 'oauth2', desc: 'Client credentials grant: `client_id`, client secret, `token_url`, `scopes`. Only the secret is secret — the rest are not, per RFC 6749.' }
				]
			},
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'How it appears in pickers.' },
					{ name: 'type', type: 'string', desc: 'Free-form context tag — `ssh`, `git_token`, `netbox`.' },
					{ name: 'auth_method', type: 'string', required: true, desc: 'One of the five above; decides which fields apply.' },
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
					{ method: 'POST', path: '/api/credential', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/credential/{id}', role: 'Admin', desc: 'Update. Omitted secret fields keep their stored values.' },
					{ method: 'DELETE', path: '/api/credential/{id}', role: 'Admin', desc: 'Delete.' }
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
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'The lookup key: 2–64 chars, lowercase letters/digits/hyphens/underscores. Referenced as `${secret:secret:<name>:value}`. Immutable, so a reference written once keeps pointing at the same thing.' },
					{ name: 'description', type: 'string | null', desc: 'What this credential authorizes, for whoever inherits it.' },
					{ name: 'value', type: 'string', desc: '**Write-only.** Encrypted at rest and never returned. Rotate by writing a new one; omit it on update to edit the description alone.' },
					{ name: 'has_value', type: 'boolean', desc: 'Read-only. Whether a value is stored — the only thing the API tells you about it.' },
					{ name: 'created_by', type: 'string | null', desc: 'Read-only. Who created the entry.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/secrets', role: 'Admin', desc: 'List with `has_value` flags.' },
					{ method: 'POST', path: '/api/secrets', role: 'Admin', desc: 'Create. 409 if the name is taken.' },
					{ method: 'GET', path: '/api/secrets/{id}', role: 'Admin', desc: 'One entry, metadata only.' },
					{ method: 'PUT', path: '/api/secrets/{id}', role: 'Admin', desc: 'Edit the description, or rotate the value by sending one.' },
					{ method: 'DELETE', path: '/api/secrets/{id}', role: 'Admin', desc: 'Delete. Soft: the name stays reserved and audit entries keep resolving.' }
				]
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'The other reference sources',
				text: 'The same resolver reads more than this table. `${secret:credential:<name>:password}` (also `username`, `private_key`, `passphrase`, `token`, `client_secret`) pulls from a stored credential, `${secret:ai_provider:<name>:api_key}` from a provider, and `${secret:integration:<name>:<path.in.auth>}` from an integration’s auth config. An unresolved reference is left literal on purpose, so it shows up in the logs instead of being sent as an empty credential.'
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
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'error_pattern', type: 'string', required: true, desc: 'What the failure looks like — the matcher.' },
					{ name: 'error_category / category', type: 'string', desc: 'Classification, for grouping and reporting.' },
					{ name: 'service_type / tool_name', type: 'string', desc: 'Where it applies. Narrow this so a fix does not leak into unrelated tools.' },
					{ name: 'fix_strategy', type: 'string', required: true, desc: 'What to do instead.' },
					{ name: 'confidence', type: 'number', desc: 'How much to trust it. Adjusted by outcomes.' },
					{ name: 'success_count / failure_count', type: 'number', desc: 'Read-only. Whether the fix actually works.' },
					{ name: 'is_system', type: 'boolean', desc: 'Shipped with the product rather than learned here.' },
					{ name: 'is_active', type: 'boolean', desc: 'Retire a learning without deleting the evidence.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/learnings', role: 'Admin', desc: 'List with hit counts.' },
					{ method: 'POST', path: '/api/learnings', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/learnings/{id}', role: 'Admin', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/learnings/{id}', role: 'Admin', desc: 'Delete.' }
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
			'Community-authored content is the intended path — take, modify, validate. The loader is the validation half: it checks a template functionally and for security patterns, and records every verdict.',
		keywords: 'validation template security upload hot reload community authoring',
		blocks: [
			{
				kind: 'params',
				title: 'Validation record',
				rows: [
					{ name: 'kind', type: 'string', desc: 'What was validated — a skill or a spec.' },
					{ name: 'target_name', type: 'string', desc: 'Which one.' },
					{ name: 'ok', type: 'boolean', desc: 'Overall verdict.' },
					{ name: 'issues', type: 'array', desc: 'What was found, each with a severity and a location.' },
					{ name: 'user_id / at', type: 'various', desc: 'Who ran it and when.' }
				]
			},
			{
				kind: 'prose',
				text: 'Validation is a dry run — nothing is installed by validating. The history exists so "was this reviewed before it went in?" has an answer that does not depend on memory.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'POST', path: '/api/loader/validate', role: 'Admin', desc: 'Validate a template and record the verdict.' },
					{ method: 'GET', path: '/api/loader/validations', role: 'Admin', desc: 'Validation history.' }
				]
			}
		]
	}
];
