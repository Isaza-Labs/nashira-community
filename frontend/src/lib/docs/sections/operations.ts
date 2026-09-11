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
					'Your message plus the active profile\'s prompt skills form the context.',
					'The model may call tools. Each call is classified by risk before it runs.',
					'Reads execute autonomously. Mutations ask you to confirm; destructive operations ask with an elevated confirmation. Anything above your permissions is refused, not requested.',
					'The reply streams back over SSE, with tool activity shown inline.'
				]
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'Bulk and irreversible changes become workflows',
				text: 'The agent does not improvise a hundred device changes as a hundred tool calls. It materialises a workflow you can read before anything executes.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'POST', path: '/api/ai/chat', role: 'Viewer', desc: 'Send a turn. Streams the response; pass a `conversation_id` to continue an existing thread.' },
					{ method: 'GET', path: '/api/ai/conversations', role: 'Viewer', desc: 'Your conversation list.' },
					{ method: 'GET', path: '/api/ai/conversations/{id}', role: 'Viewer', desc: 'Full message history for one conversation.' },
					{ method: 'DELETE', path: '/api/ai/conversations/{id}', role: 'Viewer', desc: 'Delete a conversation.' }
				]
			},
			{
				kind: 'prose',
				text: 'What the agent knows and how it answers is configured elsewhere: **AI providers** (which model), **prompt skills** (operational knowledge), **API specs** (which external calls it can make), **profiles** (which skills a given user gets) and **permissions** (which tool domains they may use).'
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
					{ name: 'device_name', type: 'string', required: true, desc: 'Unique within the tenant. Defaults to the IP when omitted on create.' },
					{ name: 'ip_address', type: 'string', required: true, desc: 'Validated as a real IP. This is what SSH and ping use.' },
					{ name: 'platform', type: 'string', desc: 'Drives vendor command resolution — `ios`, `nxos`, `eos`, `junos`, `linux`, …' },
					{ name: 'vendor', type: 'string', desc: 'Cisco, Arista, Juniper… informational, useful for filtering.' },
					{ name: 'os_version', type: 'string', desc: 'Reported or recorded software version.' },
					{ name: 'site', type: 'string', desc: 'Location. Commonly used as a pool filter.' },
					{ name: 'role', type: 'string', desc: 'core, edge, access… matched by the `device_role` policy clause.' },
					{ name: 'status', type: 'string', desc: 'Reachability as last observed.' },
					{ name: 'credential_id', type: 'uuid | null', desc: 'Which stored credential to connect with. The value itself never leaves the server.' },
					{ name: 'allow_draft', type: 'boolean', default: 'true', desc: 'May a draft workflow target this device?' },
					{ name: 'allow_qa', type: 'boolean', default: 'true', desc: 'May a qa workflow target it?' },
					{ name: 'allow_production', type: 'boolean', default: 'true', desc: 'May a production workflow target it?' },
					{ name: 'source_id', type: 'uuid | null', desc: 'The inventory source that created it, when synced rather than typed.' },
					{ name: 'external_id', type: 'string | null', desc: 'Its id in that source. Paired with `source_id` this is the sync identity — which is why renaming a device upstream no longer duplicates it.' },
					{ name: 'last_sync_at', type: 'timestamp | null', desc: 'Last time the source refreshed it.' },
					{ name: 'properties', type: 'object | null', desc: 'Free-form attributes carried over from the source.' },
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
					{ method: 'GET', path: '/api/device', role: 'Viewer', desc: 'List with search and pagination.' },
					{ method: 'GET', path: '/api/device/{id}', role: 'Viewer', desc: 'One device.' },
					{ method: 'POST', path: '/api/device', role: 'Operator', desc: 'Create. Refuses a duplicate name or IP in the tenant.' },
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
					{ name: 'name', type: 'string', required: true, desc: 'Also the handle policies match with the `device_pool` clause.' },
					{ name: 'description', type: 'string | null', desc: 'What the pool is for.' },
					{ name: 'static_members', type: 'uuid[]', default: '[]', desc: 'Device ids that are always in the pool.' },
					{ name: 'filter_rules', type: 'JSON string | null', desc: 'Attribute matching — site, role, platform, vendor. Evaluated live at resolve time.' },
					{ name: 'allow_draft', type: 'boolean', default: 'true', desc: 'Environment trio, same meaning as on a device. It narrows: a pool that allows production cannot re-enable a device that forbids it.' },
					{ name: 'allow_qa', type: 'boolean', default: 'true', desc: '—' },
					{ name: 'allow_production', type: 'boolean', default: 'true', desc: '—' },
					{ name: 'member_count', type: 'number', desc: 'Read-only. Static plus rule-matched members as of this response.' }
				]
			},
			{
				kind: 'note',
				tone: 'primary',
				title: 'Membership resolves when it is used, not when it is written',
				text: 'A run that targets a pool expands it when it starts, not when the pool was written into the workflow or trigger. Resolving early would run against membership as it stood days ago — exactly what a rule-based pool exists to prevent.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/device-pools', role: 'Viewer', desc: 'List pools with member counts.' },
					{ method: 'GET', path: '/api/device-pools/{id}', role: 'Viewer', desc: 'One pool.' },
					{ method: 'GET', path: '/api/device-pools/{id}/members', role: 'Viewer', desc: 'Resolve membership now — static plus rule matches.' },
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
					{ name: 'kind', type: 'string', required: true, desc: 'Source type. `netbox` is implemented.' },
					{ name: 'base_url', type: 'string', required: true, desc: 'Absolute http(s) URL of the source API.' },
					{ name: 'token_secret_ref', type: 'string | null', desc: 'The **name** of a stored secret holding the API token (or a full `${secret:...}` reference) — never the token itself. Raw tokens are rejected; the form stores a pasted token in the secret store first.' },
					{ name: 'site_filter', type: 'string | null', desc: 'Restrict the sync to one site.' },
					{ name: 'allow_private_network', type: 'boolean', desc: 'Permit an RFC-1918 / loopback target. Normal for an on-prem NetBox; see the SSRF guard note.' },
					{ name: 'last_synced_at', type: 'timestamp | null', desc: 'Read-only.' }
				]
			},
			{
				kind: 'params',
				title: 'Sync result',
				rows: [
					{ name: 'created / updated / unchanged / total', type: 'number', desc: 'Per-device outcome counts.' },
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
					{ method: 'POST', path: '/api/inventory/sources', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/inventory/sources/{id}', role: 'Admin', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/inventory/sources/{id}', role: 'Admin', desc: 'Delete.' },
					{ method: 'POST', path: '/api/inventory/sources/{id}/sync', role: 'Operator', desc: 'Run a sync. Supports a dry run that reports counts without writing.' }
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
					{ name: 'content', type: 'string', required: true, desc: 'Markdown body.' },
					{ name: 'tags', type: 'string[]', default: '[]', desc: 'Free-form labels used for filtering.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/knowledge', role: 'Viewer', desc: 'List and search articles.' },
					{ method: 'GET', path: '/api/knowledge/{id}', role: 'Viewer', desc: 'One article.' },
					{ method: 'POST', path: '/api/knowledge', role: 'Operator', desc: 'Create.' },
					{ method: 'PUT', path: '/api/knowledge/{id}', role: 'Operator', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/knowledge/{id}', role: 'Operator', desc: 'Delete.' }
				]
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
					{ method: 'POST', path: '/api/git/repositories', role: 'Admin', desc: 'Register and clone.' },
					{ method: 'PUT', path: '/api/git/repositories/{id}', role: 'Admin', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/git/repositories/{id}', role: 'Admin', desc: 'Remove.' }
				]
			},
			{
				kind: 'endpoints',
				title: 'Working with a repository (Operator)',
				rows: [
					{ method: 'GET', path: '/api/git/repositories/{id}/status', role: 'Viewer', desc: 'Branch, ahead/behind, dirty files.' },
					{ method: 'GET', path: '/api/git/repositories/{id}/files', role: 'Viewer', desc: 'Browse the tree.' },
					{ method: 'GET', path: '/api/git/repositories/{id}/file', role: 'Viewer', desc: 'Read one file by path.' },
					{ method: 'PUT', path: '/api/git/repositories/{id}/file', role: 'Operator', desc: 'Write one file into the working copy.' },
					{ method: 'GET', path: '/api/git/repositories/{id}/diff', role: 'Viewer', desc: 'Pending changes.' },
					{ method: 'POST', path: '/api/git/repositories/{id}/commit', role: 'Operator', desc: 'Commit the working copy with a message.' },
					{ method: 'POST', path: '/api/git/repositories/{id}/pull', role: 'Operator', desc: 'Fetch and fast-forward.' },
					{ method: 'POST', path: '/api/git/repositories/{id}/push', role: 'Operator', desc: 'Push the current branch.' },
					{ method: 'GET', path: '/api/git/repositories/{id}/branches', role: 'Viewer', desc: 'List branches.' },
					{ method: 'POST', path: '/api/git/repositories/{id}/checkout', role: 'Operator', desc: 'Switch branch.' }
				]
			},
			{
				kind: 'params',
				title: 'Webhook fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Unique within the repository.' },
					{ name: 'provider', type: 'github | gitlab | generic', default: 'github', desc: 'Decides which header carries the signature. Not editable afterwards — changing it would reject every delivery from a hook still configured the old way.' },
					{ name: 'route', type: 'string', desc: 'Read-only. Random path segment under /api/git/hooks/. Random rather than derived, because a guessable route plus allow_unsigned would be an open trigger.' },
					{ name: 'on_push_workflow_id', type: 'uuid | null', desc: 'Workflow enqueued on a matching push. Its input carries repository_id, branch and commit_sha.' },
					{ name: 'on_push_branches', type: 'string[]', default: '[]', desc: 'Exact branch names. Empty fires on every branch. A tag push carries no branch, so a filtered webhook ignores tags.' },
					{ name: 'auto_pull', type: 'boolean', default: 'true', desc: 'Pull the working copy before dispatching, so the run sees the pushed commit.' },
					{ name: 'allow_unsigned', type: 'boolean', default: 'false', desc: 'Only meaningful with no secret set. Leaving it false is what stops the route being an unauthenticated way to run a workflow.' }
				]
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
					{ name: 'retain_days', type: 'number | null', desc: 'Sets `expires_at`. Null keeps the report indefinitely.' },
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
			'The take-it-away format — an inventory list for a meeting, a PDF report for a ticket. Ask the agent to export a table (csv, xlsx, pdf, html, markdown) or to write a report as a document (pdf, html, markdown), then download it here or from the chat.',
		keywords: 'csv xlsx pdf report document spreadsheet download artifact excel html markdown',
		blocks: [
			{
				kind: 'params',
				title: 'Artifact fields',
				rows: [
					{ name: 'file_name', type: 'string', desc: 'Name offered on download.' },
					{ name: 'content_type', type: 'string', desc: 'PDF, XLSX, CSV, HTML or Markdown.' },
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
				text: 'Export = a file generated on demand, table or document, kept until someone deletes it. Report = evidence with a retention policy. If someone might ask for it again in three months, store a report.'
			}
		]
	},
	{
		slug: 'themes',
		title: 'Themes',
		group: 'Operations',
		module: 'core',
		tagline: 'Recolour the console without breaking contrast.',
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
					{ value: 'secondary', desc: 'Information, progress and focus signals (stock: the brand turquesa).' },
					{ value: 'tertiary', desc: 'A warm accent, used sparingly (stock: the brand beige).' },
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
					{ name: 'is_shared', type: 'boolean', default: 'false', desc: 'Visible to everyone. Sharing and un-sharing are both admin acts.' },
					{ name: 'is_mine', type: 'boolean', desc: 'Read-only. Whether you own it.' }
				]
			},
			{
				kind: 'list',
				items: [
					'The applied theme is stored per browser and written as inline custom properties on `<html>`, so it wins over the stylesheet without editing it.',
					'Light/dark mode is **orthogonal** — a theme works in both, and the editor can preview either without changing your own mode.',
					'The editor and the theme cards render a real slice of the interface — buttons, badges, inputs, a table — painted with the theme\'s own tokens, using the same ramp function `apply()` uses. What you see is by construction what you get.'
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/themes', role: 'Viewer', desc: 'Your themes plus every shared one.' },
					{ method: 'POST', path: '/api/themes', role: 'Viewer', desc: 'Create. `is_shared: true` requires admin.' },
					{ method: 'PUT', path: '/api/themes/{id}', role: 'Viewer', desc: 'Update your own. A shared theme belongs to everybody, so editing one is an admin act even for its author.' },
					{ method: 'DELETE', path: '/api/themes/{id}', role: 'Viewer', desc: 'Delete, same ownership rule.' }
				]
			}
		]
	}
];
