import type { DocSection } from '../types';

export const operations: DocSection[] = [
	{
		slug: 'chat',
		title: 'Chat',
		group: 'Operations',
		module: 'chat',
		tagline: 'The agent, with the whole console available as tools.',
		uiPath: '/chat',
		apiBase: '/api/ai',
		role: 'Viewer',
		purpose:
			'Ask for what you want in plain language. The agent reads inventory, runs diagnostics, drafts workflows and explains what it found — using the same REST API the screens use, under the same permissions you already have.',
		keywords: 'agent llm conversation assistant ask tools streaming',
		blocks: [
			{ kind: 'heading', text: 'How a turn works' },
			{
				kind: 'steps',
				items: [
					'Your message, any attached files, the prompt skills and your profile (tone and depth) form the context.',
					'The model may call tools. Each call is classified by risk before it runs.',
					'Reads execute autonomously. Mutations ask you to confirm; destructive operations ask with an elevated confirmation. Anything above your permissions is refused, not requested. The tiers are listed below.',
					'A confirmation-gated call pauses the turn with a `confirmation_required` frame. Send the turn again with the tool name in `approvals` to go ahead; that approval then holds for the rest of the conversation. A call that turns out to be a read — a `GET`/`HEAD` operation from an uploaded spec, or an MCP tool marked read-only on a trusted server — runs without asking, but an `elevated_confirm` tool is never waived that way.',
					'The reply streams back over SSE, with tool activity shown inline.'
				]
			},
			{
				kind: 'values',
				title: 'Autonomy tiers',
				rows: [
					{ value: 'autonomous', desc: 'Runs without asking — reads and diagnostics.' },
					{ value: 'single_confirm', desc: 'Asks you once. Most mutations.' },
					{ value: 'elevated_confirm', desc: 'Asks with a heightened confirmation. Destructive operations.' },
					{ value: 'human_only', desc: 'Never run by the agent — it tells you to do it from the console.' }
				]
			},
			{
				kind: 'prose',
				text: 'Every tool call that is not `autonomous` spends one slot of the turn\'s mutation budget (20 by default). When it runs out, the agent stops, summarises what changed and asks you to re-authorise before more mutations. A tool missing from the permission matrix is refused.'
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'Bulk and irreversible changes become workflows',
				text: 'The agent does not improvise a hundred device changes as a hundred tool calls. It materialises a workflow you can read before anything executes.'
			},
			{
				kind: 'params',
				title: 'Turn fields',
				rows: [
					{ name: 'message', type: 'string', required: true, desc: 'What you are asking.' },
					{ name: 'conversation_id', type: 'uuid | null', desc: 'Continue an existing thread. Omitted, a new conversation starts.' },
					{ name: 'attachments', type: 'array | null', desc: 'Files for this turn, as `{ filename, content_base64 }`. They are kept with the conversation, and the agent reads them with its `parse_file` tool, in this turn or a later one.' },
					{ name: 'approvals', type: 'string[] | null', desc: 'Tool names you confirm, so a paused call can run. Approvals accumulate for the conversation.' },
					{ name: 'provider_id / model', type: 'uuid / string | null', desc: 'Which provider and model answer, from `GET /api/ai/models`. Omitted, the conversation keeps its last choice; a new one uses the default provider.' }
				]
			},
			{
				kind: 'values',
				title: 'Stream frames',
				rows: [
					{ value: 'conversation', desc: 'The conversation id, and whether it was just created.' },
					{ value: 'model', desc: 'The provider and model answering this turn.' },
					{ value: 'token', desc: 'A piece of the answer text.' },
					{ value: 'tool_start / tool_result', desc: 'A tool call and its outcome, shown inline.' },
					{ value: 'confirmation_required', desc: 'A call is waiting for your approval — the tool name, its arguments and its tier.' },
					{ value: 'done', desc: 'End of the turn, with token counts.' },
					{ value: 'error', desc: 'The turn failed, with a message and a code.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'POST', path: '/api/ai/chat', role: 'Viewer', desc: 'Send a turn. The response is a Server-Sent Events stream of the frames above.' },
					{ method: 'GET', path: '/api/ai/models', role: 'Viewer', desc: 'The models you can pick: those of enabled providers, without any keys.' },
					{ method: 'GET', path: '/api/ai/conversations', role: 'Viewer', desc: 'Your conversation list, with `status`, `tokens_in/out` and the `ai_provider_id` and `model` that last answered.' },
					{ method: 'GET', path: '/api/ai/conversations/{id}', role: 'Viewer', desc: 'Full message history for one conversation.' },
					{ method: 'DELETE', path: '/api/ai/conversations/{id}', role: 'Viewer', desc: 'Soft delete, attachments included — an admin can restore the thread from the audit trail.' }
				]
			},
			{
				kind: 'params',
				title: 'Limits (appsettings)',
				rows: [
					{ name: 'AiChat:StreamDeadlineSeconds', type: 'number', default: '240', desc: 'How long one turn may run. A proxy in front of the backend needs a longer read timeout than this.' },
					{ name: 'AiChat:HistoryCharBudget', type: 'number', default: '60000', desc: 'How much earlier conversation is sent back to the model.' },
					{ name: 'AiChat:MaxToolResultChars', type: 'number', default: '100000', desc: 'Longest single tool result the model sees; longer results are cut with a marker.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'A cut-off answer says so',
				text: 'When the model runs out of output tokens, the text that arrived is kept with a notice appended, and the turn is recorded as `truncated` rather than answered. Bulk deletions by the agent take an `expected_count`: if the matching set changed since you confirmed it, nothing is deleted.'
			},
			{
				kind: 'prose',
				text: 'What the agent knows and how it answers is configured elsewhere: **AI providers** (which model), **prompt skills** (operational knowledge), **API specs** (which external calls it can make), **profiles** (the tone and depth a given user gets) and **permissions** (which tool domains they may use).'
			}
		]
	},
	{
		slug: 'devices',
		title: 'Devices',
		group: 'Operations',
		module: 'fleet',
		tagline: 'The inventory every run targets, and the environment trio that protects it.',
		uiPath: '/devices',
		apiBase: '/api/device',
		role: 'Viewer',
		purpose:
			'A device is a piece of network equipment Nashira can reach. It carries how to connect (credentials, platform), where it sits (site, role), and — importantly — which workflow environments are allowed to touch it.',
		keywords: 'inventory hosts switches routers nodes ssh targets netbox',
		blocks: [
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'device_name', type: 'string', required: true, desc: 'Unique among active devices. (The agent\'s `create_device` tool fills it with the IP when you leave it out; the API does not.)' },
					{ name: 'ip_address', type: 'string', required: true, desc: 'What SSH and ping use. The API stores it as given — only the agent\'s tool checks it is a real IP — and does not refuse an address another device already has.' },
					{ name: 'platform', type: 'string', desc: 'The Netmiko `device_type`: it picks the SSH driver and is the key vendor commands resolve on — `cisco_ios`, `cisco_nxos`, `cisco_xe`, `arista_eos`, `juniper_junos`, `linux`, … A short name such as `ios` resolves nothing.' },
					{ name: 'vendor', type: 'string', desc: 'Cisco, Arista, Juniper… informational, useful for filtering.' },
					{ name: 'os_version', type: 'string', desc: 'Reported or recorded software version.' },
					{ name: 'site', type: 'string', desc: 'Location. Commonly used as a pool filter.' },
					{ name: 'role', type: 'string', desc: 'core, edge, access… matched by the `device_role` policy clause.' },
					{ name: 'status', type: 'string', desc: 'Reachability as last observed.' },
					{ name: 'credential_id', type: 'uuid | null', desc: 'Which stored credential to connect with. The value itself never leaves the server.' },
					{ name: 'allow_draft', type: 'boolean', default: 'true', desc: 'May a draft workflow target this device?' },
					{ name: 'allow_qa', type: 'boolean', default: 'false', desc: 'May a qa workflow target it? Off by default, so exposing a device to qa is a deliberate step — turn it on before promoting a workflow that targets it.' },
					{ name: 'allow_production', type: 'boolean', default: 'true', desc: 'May a production workflow target it?' },
					{ name: 'source_id', type: 'uuid | null', desc: 'The inventory source that created it, when synced rather than typed.' },
					{ name: 'external_id', type: 'string | null', desc: 'Its id in that source. Paired with `source_id` this is the sync identity — which is why renaming a device upstream no longer duplicates it.' },
					{ name: 'last_sync_at', type: 'timestamp | null', desc: 'Last time the source refreshed it.' },
					{ name: 'properties', type: 'object | null', desc: 'Free-form attributes carried over from the source.' },
					{ name: 'expected_ssh_host_key_fingerprint', type: 'string | null', desc: '**Write-only.** The SSH host key to pin, as `SHA256:<base64>`. With it set, the SSH runner refuses a host presenting any other key; without it, the first key seen is trusted.' },
					{ name: 'has_host_key_fingerprint', type: 'boolean', desc: 'Read-only. Whether an SSH host key is pinned for this device.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'Turning an allow flag off is a real control',
				text: 'It does not filter the device out of a run — it makes the run refuse, naming the device. Use it to fence production hardware off from drafts, not to tidy up target lists.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/device', role: 'Viewer', desc: 'List. Filters `site` and `role`; `q` searches name, IP, site and role; `limit`/`offset` paginate.' },
					{ method: 'GET', path: '/api/device/stats', role: 'Viewer', desc: 'Total and count per status across the whole inventory — what Overview shows.' },
					{ method: 'GET', path: '/api/device/{id}', role: 'Viewer', desc: 'One device.' },
					{ method: 'POST', path: '/api/device', role: 'Operator', desc: 'Create. A duplicate name is refused with 409; a duplicate IP is not.' },
					{ method: 'PUT', path: '/api/device/{id}', role: 'Operator', desc: 'Partial update.' },
					{ method: 'DELETE', path: '/api/device/{id}', role: 'Operator', desc: 'Soft delete.' }
				]
			},
			{
				kind: 'prose',
				text: 'Devices arrive two ways: typed in here, or synced from an **inventory source**. Group them with **device pools** so a workflow can target "all core routers" instead of a list that ages.'
			}
		]
	},
	{
		slug: 'device-pools',
		title: 'Device pools',
		group: 'Operations',
		module: 'fleet',
		tagline: 'Named target sets — static membership, filter rules, or both.',
		uiPath: '/admin/pools',
		apiBase: '/api/device-pools',
		role: 'Viewer',
		purpose:
			'A pool answers "which devices does this apply to?" once, so workflows, triggers and policies can refer to a group by name instead of carrying a device list that silently goes stale.',
		keywords: 'groups targets rules filter static membership fleet',
		blocks: [
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Also the handle policies match with the `device_pool` clause. Unique among active pools (409 `pool_name_taken`).' },
					{ name: 'description', type: 'string | null', desc: 'What the pool is for.' },
					{ name: 'static_members', type: 'uuid[]', default: '[]', desc: 'Device ids that are always in the pool.' },
					{ name: 'filter_rules', type: 'JSON string | null', desc: 'A JSON object, sent as a string, keyed by `site`, `role`, `vendor`, `platform` or `status`. An unknown key is refused with 400 rather than ignored — it would otherwise match everything. Evaluated live at resolve time.' },
					{ name: 'slug', type: 'string', desc: 'Read-only.' },
					{ name: 'allow_draft', type: 'boolean', default: 'true', desc: 'Environment trio, same meaning as on a device. It narrows: a pool that allows production cannot re-enable a device that forbids it.' },
					{ name: 'allow_qa', type: 'boolean', default: 'false', desc: 'Off by default, like on a device.' },
					{ name: 'allow_production', type: 'boolean', default: 'true', desc: '—' },
					{ name: 'member_count', type: 'number', desc: 'Read-only. Static plus rule-matched members as of this response.' }
				]
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'Membership resolves when it is used, not when it is written',
				text: 'A pool is resolved each time something asks — the members endpoint below, or a policy\'s `device_pool` clause when a run is checked — never when it was written. Resolving early would answer with membership as it stood days ago, which is exactly what a rule-based pool exists to prevent. A run itself takes a list of device ids: pools are not expanded into its targets.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/device-pools', role: 'Viewer', desc: 'List pools with member counts.' },
					{ method: 'GET', path: '/api/device-pools/{id}', role: 'Viewer', desc: 'One pool.' },
					{ method: 'GET', path: '/api/device-pools/{id}/members', role: 'Viewer', desc: 'Resolve membership now — static plus rule matches. With `?environment=draft|qa|production` it answers what a run in that environment could touch: `pool_allows_environment`, the allowed `members`, and `excluded` with the reason for each device left out.' },
					{ method: 'POST', path: '/api/device-pools', role: 'Operator', desc: 'Create.' },
					{ method: 'PUT', path: '/api/device-pools/{id}', role: 'Operator', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/device-pools/{id}', role: 'Operator', desc: 'Soft delete.' }
				]
			}
		]
	},
	{
		slug: 'inventory',
		title: 'Inventory sources',
		group: 'Operations',
		module: 'fleet',
		tagline: 'Keep the device list in step with the system of record.',
		uiPath: '/inventory',
		apiBase: '/api/inventory/sources',
		role: 'Viewer',
		purpose:
			'Pull devices from an external inventory (NetBox today) instead of maintaining two lists that drift. A sync creates what is new, updates what changed, and leaves the rest alone.',
		keywords: 'netbox sync source import discovery cmdb',
		blocks: [
			{
				kind: 'params',
				title: 'Source fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'How the source appears in the console.' },
					{ name: 'kind', type: 'string', default: 'netbox', desc: 'Source type. `netbox` is the one implemented. Fixed after creation.' },
					{ name: 'base_url', type: 'string', required: true, desc: 'Absolute http(s) URL of the source API.' },
					{ name: 'token_secret_ref', type: 'string | null', desc: 'The **name** of a stored secret holding the API token (or a full `${secret:...}` reference) — never the token itself. Raw tokens are rejected; the form stores a pasted token in the secret store first. The sync sends it as `Authorization: Token <key>`, the NetBox convention. A source is independent of any NetBox **integration**: neither shares the other\'s credential.' },
					{ name: 'site_filter', type: 'string | null', desc: 'Restrict the sync to one site.' },
					{ name: 'allow_private_network', type: 'boolean', default: 'false', desc: 'Permit an RFC-1918 / loopback target. Off by default, so an on-prem NetBox is refused by the SSRF guard until you turn this on.' },
					{ name: 'last_synced_at', type: 'timestamp | null', desc: 'Read-only.' }
				]
			},
			{
				kind: 'params',
				title: 'Sync result',
				rows: [
					{ name: 'created / updated / unchanged / total', type: 'number', desc: 'Per-device outcome counts.' },
					{ name: 'skipped', type: 'number', desc: 'Records that could not become a device of their own — no usable name, or a name another device already holds. Counted and reported instead of aborting the sync.' },
					{ name: 'dry_run', type: 'boolean', desc: 'Whether anything was actually written.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Identity is (source, external id)',
				text: 'Matching by name meant a rename upstream created a second device. Sync now matches on the source and its external id, so a rename updates the device it should.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/inventory/sources', role: 'Viewer', desc: 'List sources.' },
					{ method: 'GET', path: '/api/inventory/sources/{id}', role: 'Viewer', desc: 'One source.' },
					{ method: 'POST', path: '/api/inventory/sources', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/inventory/sources/{id}', role: 'Admin', desc: 'Update. `kind` cannot be changed.' },
					{ method: 'DELETE', path: '/api/inventory/sources/{id}', role: 'Admin', desc: 'Soft delete.' },
					{ method: 'POST', path: '/api/inventory/sources/{id}/sync', role: 'Operator', desc: 'Run a sync. `?dryRun=true` reports the counts without writing.' }
				]
			}
		]
	},
	{
		slug: 'knowledge',
		title: 'Knowledge',
		group: 'Operations',
		module: 'knowledge',
		tagline: 'Runbooks and notes the agent can search.',
		uiPath: '/knowledge',
		apiBase: '/api/knowledge',
		role: 'Viewer',
		purpose:
			'Institutional knowledge in one searchable place — procedures, escalation paths, why a particular device is special. The agent searches it while answering, so writing an article changes what chat knows.',
		keywords: 'articles docs runbook wiki notes procedures search',
		blocks: [
			{
				kind: 'params',
				title: 'Article fields',
				rows: [
					{ name: 'title', type: 'string', required: true, desc: 'Also the source of the slug; renaming regenerates it.' },
					{ name: 'slug', type: 'string', desc: 'Read-only, derived from the title.' },
					{ name: 'content', type: 'string', default: '""', desc: 'Markdown body. Only `title` is required.' },
					{ name: 'tags', type: 'string[]', default: '[]', desc: 'Free-form labels. The console filters on them; the API search does not.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/knowledge', role: 'Viewer', desc: 'List articles. `q` searches title and body; `limit`/`offset` paginate.' },
					{ method: 'GET', path: '/api/knowledge/{id}', role: 'Viewer', desc: 'One article.' },
					{ method: 'POST', path: '/api/knowledge', role: 'Operator', desc: 'Create.' },
					{ method: 'PUT', path: '/api/knowledge/{id}', role: 'Operator', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/knowledge/{id}', role: 'Operator', desc: 'Soft delete — an admin can restore it from the audit trail.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Seeded guides',
				text: 'An install starts with a set of vendor and platform guides loaded as ordinary articles. They are seeded once, keyed by slug: edit them freely, and neither an edit nor a deletion is undone by the next restart.'
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Knowledge vs prompt skills',
				text: 'Knowledge is looked up when relevant; a prompt skill is always in the agent\'s context. Put a procedure in knowledge; put "always confirm the change window before touching core" in a skill.'
			}
		]
	},
	{
		slug: 'git',
		title: 'Git repositories',
		group: 'Operations',
		module: 'git',
		tagline: 'Configuration in version control, driven from the console.',
		uiPath: '/git',
		apiBase: '/api/git',
		role: 'Viewer',
		purpose:
			'Register repositories that hold configuration or automation content, then browse, edit, commit and push without leaving Nashira. Useful for config backups and for keeping generated artifacts reviewable.',
		keywords: 'repository commit branch push pull diff version control backup',
		blocks: [
			{
				kind: 'params',
				title: 'Repository fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Display name in the console.' },
					{ name: 'url', type: 'string', required: true, desc: 'Clone URL. https only — the SSH transport is not implemented, and an ssh:// URL is rejected on save.' },
					{ name: 'description', type: 'string | null', desc: 'What the repository holds.' },
					{ name: 'default_branch', type: 'string', default: 'main', desc: 'Branch checked out after clone.' },
					{ name: 'auth_credential_id', type: 'uuid | null', desc: 'Stored credential holding the personal access token. Leave empty for a public repository.' },
					{ name: 'local_path', type: 'string | null', desc: 'Read-only. Where the working copy lives on the server.' },
					{ name: 'last_fetched_at', type: 'timestamp | null', desc: 'Read-only.' }
				]
			},
			{
				kind: 'endpoints',
				title: 'Repository management (Admin)',
				rows: [
					{ method: 'GET', path: '/api/git/repositories', role: 'Viewer', desc: 'List repositories.' },
					{ method: 'GET', path: '/api/git/repositories/{id}', role: 'Viewer', desc: 'One repository.' },
					{ method: 'POST', path: '/api/git/repositories', role: 'Admin', desc: 'Register and clone.' },
					{ method: 'PUT', path: '/api/git/repositories/{id}', role: 'Admin', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/git/repositories/{id}', role: 'Admin', desc: 'Soft delete.' }
				]
			},
			{
				kind: 'endpoints',
				title: 'Working with a repository (Operator)',
				rows: [
					{ method: 'GET', path: '/api/git/repositories/{id}/status', role: 'Viewer', desc: 'Branch, `clean`, and the `staged`, `modified`, `untracked` and `missing` files. `ahead`/`behind` are null when the branch has no upstream.' },
					{ method: 'GET', path: '/api/git/repositories/{id}/files', role: 'Viewer', desc: 'Browse the tree. `?path=&ref=` pick the folder and the revision.' },
					{ method: 'GET', path: '/api/git/repositories/{id}/file', role: 'Viewer', desc: 'Read one file: `?path=` (required) and optional `ref`. Binary files are flagged `is_binary`.' },
					{ method: 'PUT', path: '/api/git/repositories/{id}/file', role: 'Operator', desc: 'Write one file **and commit it**. Body `{ path, content, commit_message, author_name, author_email, branch, push }` — `commit_message` is required, and `push: true` also pushes. Files above the `Git:MaxFileBytes` setting (5 MB by default) are refused, on write as on read.' },
					{ method: 'GET', path: '/api/git/repositories/{id}/diff', role: 'Viewer', desc: 'Pending changes, or the diff between `?from=` and `?to=`, optionally narrowed by `path`.' },
					{ method: 'POST', path: '/api/git/repositories/{id}/commit', role: 'Operator', desc: 'Commit the working copy. Body `{ commit_message, author_name, author_email, paths, push }` — `commit_message` is required, `paths` commits only those files, `push: true` also pushes.' },
					{ method: 'POST', path: '/api/git/repositories/{id}/pull', role: 'Operator', desc: 'Fetch and merge — fast-forward when possible, a merge commit otherwise; a conflict answers `ok: false`. `?branch=` picks the branch.' },
					{ method: 'POST', path: '/api/git/repositories/{id}/push', role: 'Operator', desc: 'Push the current branch, or `?branch=`.' },
					{ method: 'GET', path: '/api/git/repositories/{id}/branches', role: 'Viewer', desc: 'List branches.' },
					{ method: 'POST', path: '/api/git/repositories/{id}/checkout', role: 'Operator', desc: 'Switch to `?branch=` (required).' }
				]
			},
			{
				kind: 'params',
				title: 'Webhook fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Unique within the repository.' },
					{ name: 'provider', type: 'github | gitlab | generic', default: 'github', desc: 'Decides which header carries the signature. Not editable afterwards — changing it would reject every delivery from a hook still configured the old way.' },
					{ name: 'route', type: 'string', desc: 'Read-only. Random path segment under /api/git/hooks/. Random rather than derived, because a guessable route plus allow_unsigned would be an open trigger.' },
					{ name: 'on_push_workflow_id', type: 'uuid | null', desc: 'Workflow enqueued on a matching push. Its input carries repository_id, branch and commit_sha. On update, send `clear_on_push_workflow: true` to unbind it.' },
					{ name: 'on_push_branches', type: 'string[]', default: '[]', desc: 'Exact branch names. Empty fires on every branch. A tag push carries no branch, so a filtered webhook ignores tags.' },
					{ name: 'auto_pull', type: 'boolean', default: 'true', desc: 'Pull the working copy before dispatching, so the run sees the pushed commit.' },
					{ name: 'allow_unsigned', type: 'boolean', default: 'false', desc: 'Only consulted when the webhook has no secret — and every webhook is created with one and keeps one through rotation, so today this flag has no effect. Every delivery must be signed.' },
					{ name: 'enabled', type: 'boolean', default: 'true', desc: 'Stop dispatching without removing the hook.' },
					{ name: 'ingest_path / signature_header', type: 'string', desc: 'Read-only. The URL path to give the provider, and the header its signature must arrive in: `X-Hub-Signature-256` for github, `X-Gitlab-Token` for gitlab, `X-Nashira-Signature` for generic.' },
					{ name: 'has_secret / on_push_workflow_name / last_delivery_at / last_delivery_status / delivery_count', type: 'various', desc: 'Read-only state.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Configure the provider from the response',
				text: 'Copy `ingest_path` and `signature_header` from the webhook as created, not from memory. A redelivery is recognised by `X-GitHub-Delivery` or `X-Nashira-Delivery` and does not enqueue a second run.'
			},
			{
				kind: 'endpoints',
				title: 'Webhooks',
				rows: [
					{ method: 'GET', path: '/api/git/repositories/{id}/webhooks', role: 'Viewer', desc: 'List webhooks on a repository.' },
					{ method: 'POST', path: '/api/git/repositories/{id}/webhooks', role: 'Admin', desc: 'Register one. The secret is returned once and never again.' },
					{ method: 'PUT', path: '/api/git/repositories/{id}/webhooks/{hookId}', role: 'Admin', desc: 'Update the filter, the bound workflow or the flags.' },
					{ method: 'DELETE', path: '/api/git/repositories/{id}/webhooks/{hookId}', role: 'Admin', desc: 'Remove it. The provider still has the URL — delete it there too.' },
					{ method: 'POST', path: '/api/git/repositories/{id}/webhooks/{hookId}/rotate-secret', role: 'Admin', desc: 'Issue a new secret. The old one stops working immediately.' },
					{ method: 'GET', path: '/api/git/repositories/{id}/webhooks/{hookId}/deliveries', role: 'Viewer', desc: 'Delivery history. Bodies and headers are not stored; rows older than 30 days are pruned.' },
					{ method: 'GET', path: '/api/git/repositories/{id}/webhooks/{hookId}/dry-run', role: 'Viewer', desc: 'What a push to ?branch= would do. Pulls nothing, enqueues nothing.' },
					{ method: 'POST', path: '/api/git/hooks/{route}', role: 'Public', desc: 'The inbound endpoint. Authenticated by the signature, not by session. An unknown route and a bad signature both answer 401, so the endpoint cannot be used to discover which routes are live.' }
				]
			}
		]
	},
	{
		slug: 'reports',
		title: 'Reports',
		group: 'Operations',
		module: 'artifacts',
		tagline: 'Stored artifacts with optional retention, downloadable later.',
		uiPath: '/reports',
		apiBase: '/api/reports',
		role: 'Viewer',
		purpose:
			'A report is evidence someone may want to re-read: an audit output, a compliance summary, the state of a fleet before a change. Distinct from exports, which are throwaway spreadsheets.',
		keywords: 'artifact markdown csv retention download evidence output',
		blocks: [
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'title', type: 'string', required: true, desc: 'Shown in the list; also the fallback file name.' },
					{ name: 'description', type: 'string | null', desc: 'Context for a future reader.' },
					{ name: 'content', type: 'string', required: true, desc: 'The body. Write-only — the list never streams it; use the download endpoint.' },
					{ name: 'content_type', type: 'string', default: 'text/markdown', desc: 'MIME type used on download. Markdown, plain text, CSV, JSON and HTML are offered in the UI.' },
					{ name: 'file_name', type: 'string', desc: 'Defaults to a slug of the title with a `.md` extension.' },
					{ name: 'retain_days', type: 'number | null', desc: 'Sets `expires_at`. Null, zero or a negative number keeps the report indefinitely.' },
					{ name: 'expires_at', type: 'timestamp | null', desc: 'Read-only. When the report expires; null for never.' },
					{ name: 'report_artifact_id', type: 'uuid', desc: 'Read-only.' },
					{ name: 'workflow_run_id', type: 'uuid | null', desc: 'The run that produced it, when generated by automation.' },
					{ name: 'size_bytes', type: 'number', desc: 'Read-only. The maximum accepted body is 10 MB.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Expiry hides, then the sweeper deletes',
				text: 'An expired report drops out of the default listing immediately and returns 404 on download; the hourly retention sweeper removes the row itself. Tick **Show expired** in the UI to see what is on its way out.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/reports', role: 'Viewer', desc: 'List. `includeExpired=true` shows expired rows.' },
					{ method: 'GET', path: '/api/reports/{id}/download', role: 'Viewer', desc: 'Download the body with its content type and file name.' },
					{ method: 'POST', path: '/api/reports', role: 'Operator', desc: 'Store a report.' },
					{ method: 'DELETE', path: '/api/reports/{id}', role: 'Operator', desc: 'Soft delete.' }
				]
			},
			{
				kind: 'prose',
				text: 'The agent works with reports too, under the `report` permission domain: `list_reports` and `read_report` run without asking, `save_report` asks first. A rendered PDF or XLSX is listed with `readable: false` — the agent can hand you its download link, not its text.'
			}
		]
	},
	{
		slug: 'exports',
		title: 'Exports',
		group: 'Operations',
		module: 'artifacts',
		tagline: 'Generated documents and spreadsheets, downloaded once.',
		uiPath: '/exports',
		apiBase: '/api/export',
		role: 'Viewer',
		purpose:
			'The take-it-away format — an inventory list for a meeting, a PDF report for a ticket. Ask the agent to export a table (csv, json, xlsx, docx, pdf, html, markdown) or to write a report as a document (pdf, docx, html, markdown), then download it here or from the chat.',
		keywords: 'csv xlsx pdf report document spreadsheet download artifact excel html markdown',
		blocks: [
			{
				kind: 'params',
				title: 'Artifact fields',
				rows: [
					{ name: 'export_artifact_id', type: 'uuid', desc: 'The artifact\'s id.' },
					{ name: 'file_name', type: 'string', desc: 'Name offered on download.' },
					{ name: 'content_type', type: 'string', desc: 'CSV, JSON, XLSX, DOCX, PDF, HTML or Markdown.' },
					{ name: 'download_url', type: 'string', desc: 'Where to fetch the file.' },
					{ name: 'size_bytes', type: 'number', desc: 'Size of the stored artifact.' },
					{ name: 'created_at', type: 'timestamp', desc: 'When it was generated.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/export', role: 'Viewer', desc: 'List available artifacts.' },
					{ method: 'GET', path: '/api/export/{id}/download', role: 'Viewer', desc: 'Download.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Export or report?',
				text: 'Export = a file generated on demand, table or document. There is no delete and no retention for exports today, so they are kept indefinitely. Report = evidence with a retention policy. If someone might ask for it again in three months, store a report.'
			}
		]
	},
	{
		slug: 'themes',
		title: 'Themes',
		group: 'Operations',
		module: 'core',
		tagline: 'Recolour and restyle the console without breaking contrast.',
		uiPath: '/themes',
		apiBase: '/api/themes',
		role: 'Viewer',
		purpose:
			'Pick a base colour per family and the console adopts it. Useful for telling environments apart at a glance — production in red, lab in green.',
		keywords: 'appearance colors palette branding look oklch dark mode',
		blocks: [
			{ kind: 'heading', text: 'What a theme actually changes' },
			{
				kind: 'note',
				tone: 'primary',
				title: 'It swaps hue, not colour',
				text: 'A theme picks a base colour per family. The engine keeps the original lightness of every stop in the OKLCH ramp and swaps only the hue, with chroma clamped. Because contrast is dominated by lightness, the design system\'s verified AA pairs survive recolouring — you can make Nashira purple, but not illegible.'
			},
			{
				kind: 'values',
				title: 'The seven families',
				rows: [
					{ value: 'primary', desc: 'Filled buttons, links, focus rings, active navigation.' },
					{ value: 'secondary', desc: 'Information, progress and focus signals (stock: the brand turquoise, `#00B2CA`).' },
					{ value: 'tertiary', desc: 'A warm accent, used sparingly (stock: the brand beige, `#E3CFB4`).' },
					{ value: 'success', desc: 'Healthy, passed, delivered.' },
					{ value: 'warning', desc: 'Degraded, stale, disarmed.' },
					{ value: 'error', desc: 'Failed, unreachable, denied, destructive buttons.' },
					{ value: 'surface', desc: 'The neutral base — cards, inputs, borders, and the app background and control fills, which sit deliberately off the ramp and are moved by the same hue swap.' }
				]
			},
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'How the theme appears in the picker.' },
					{ name: 'description', type: 'string | null', desc: 'What it is for.' },
					{ name: 'colors', type: 'object', required: true, desc: 'A JSON object of family → base hex. A theme is a diff: families you leave out fall back to the stock ramp.' },
					{ name: 'settings', type: 'object', default: '{}', desc: 'Style overrides, from a closed vocabulary: `font_body` (`plex`, `system`, `serif`, `mono`), `font_heading` (`inherit` or the same four), `font_mono` (`plex`, `system`), `heading_weight` (300–900), `roundness` (0–2), `ui_scale` (0.85–1.15). An unknown key is refused with `settings_unknown_key`. On update, omitted keeps the stored settings and `{}` clears them.' },
					{ name: 'is_shared', type: 'boolean', default: 'false', desc: 'Visible to everyone. Sharing and un-sharing are both admin acts.' },
					{ name: 'is_mine', type: 'boolean', desc: 'Read-only. Whether you own it.' },
					{ name: 'theme_id / updated_at', type: 'uuid / timestamp', desc: 'Read-only.' }
				]
			},
			{
				kind: 'list',
				items: [
					'The applied theme is stored per browser and written as inline custom properties on `<html>`, so it wins over the stylesheet without editing it.',
					'Light/dark mode is **orthogonal** — a theme works in both, and the editor can preview either without changing your own mode.',
					'The editor starts from a preset — Nashira, Midnight, Ember, Moss, Orchid, Graphite or High contrast. Presets keep success, warning and error in their usual bands, so a run list still reads at a glance.',
					'The editor and the theme cards render a real slice of the interface — buttons, badges, inputs, a table — painted with the theme\'s own tokens, using the same ramp function `apply()` uses. What you see is by construction what you get.'
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/themes', role: 'Viewer', desc: 'Your themes plus every shared one.' },
					{ method: 'POST', path: '/api/themes', role: 'Viewer', desc: 'Create. `is_shared: true` requires admin.' },
					{ method: 'PUT', path: '/api/themes/{id}', role: 'Viewer', desc: 'Update your own; an admin can update anyone\'s. A shared theme belongs to everybody, so editing one is an admin act even for its author.' },
					{ method: 'DELETE', path: '/api/themes/{id}', role: 'Viewer', desc: 'Soft delete, same ownership rule. Answers 204.' }
				]
			}
		]
	}
];
