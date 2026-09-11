import type { DocSection } from '../types';

export const integrations: DocSection[] = [
	{
		slug: 'integrations',
		title: 'Integrations',
		group: 'Integrations & AI',
		module: 'integrations',
		tagline: 'One external system: its URL, its credentials, and everything it can do.',
		uiPath: '/admin/integrations',
		apiBase: '/api/integrations',
		role: 'Viewer',
		purpose:
			'Register a system once — NetBox, ServiceNow, an internal API — and both the agent and the workflow builder can use it. The action catalogue is projected from the OpenAPI specs linked to it, so a single upload feeds both instead of two lists that drift apart.',
		keywords: 'external system rest api netbox servicenow catalog actions auth health oauth',
		blocks: [
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Display name; also generates the slug used in references.' },
					{ name: 'type', type: 'string', desc: 'Free-form classification — `netbox`, `itsm`, `generic`.' },
					{ name: 'base_url', type: 'string', required: true, desc: 'Absolute http(s) root. Specs linked to this integration inherit it unless they carry their own.' },
					{ name: 'auth_config', type: 'string', desc: '**Write-only.** JSON describing the method and `${secret:secret:<name>:value}` references. Never returned — echoing it back would map the secret store for free.' },
					{ name: 'headers', type: 'string | null', desc: 'Extra headers sent with every call.' },
					{ name: 'verify_ssl', type: 'boolean', default: 'true', desc: 'Turn off only for a known self-signed internal endpoint.' },
					{ name: 'allow_private_network', type: 'boolean', desc: 'Permit RFC-1918 / loopback / link-local targets. Normal on-prem; see the SSRF note.' },
					{ name: 'health_check_path', type: 'string | null', desc: 'Path the health probe hits.' },
					{ name: 'enabled', type: 'boolean', default: 'true', desc: 'Suspend use without deleting the definition.' },
					{ name: 'auth_method / has_credentials', type: 'string / boolean', desc: 'Read-only. What kind of auth is configured and whether a credential is present.' },
					{ name: 'status / last_check_error / last_checked_at', type: 'various', desc: 'Read-only. `unknown`, `healthy`, `degraded`, `unreachable`.' },
					{ name: 'spec_count / action_count', type: 'number', desc: 'Read-only. Linked specs and catalogued actions.' }
				]
			},
			{ kind: 'heading', text: 'Credentials: references, not copies' },
			{
				kind: 'note',
				tone: 'primary',
				title: 'Why integrations use `${secret:…}` while MCP servers encrypt',
				text: 'An integration points at the secret store, so rotating a token in one place updates every integration that references it. MCP credentials are encrypted in place because an OAuth token obtained at runtime has nothing to point at. The asymmetry is deliberate, not an inconsistency.'
			},
			{ kind: 'heading', text: 'The action catalogue' },
			{
				kind: 'steps',
				items: [
					'Upload an OpenAPI spec under **API specs** and link it to this integration.',
					'Run **sync actions** — each operation becomes an `IntegrationAction` with a method, path and category.',
					'Workflows call them with an `integration_action` snippet; the agent sees them as callable operations. Auth and base URL come from the integration, so neither has to repeat them.'
				]
			},
			{
				kind: 'params',
				title: 'Action fields',
				rows: [
					{ name: 'operation_id', type: 'string | null', desc: 'The OpenAPI operationId it came from.' },
					{ name: 'name / description', type: 'string', desc: 'What it does, as shown to the agent and the builder.' },
					{ name: 'method / path', type: 'string', desc: 'The HTTP call, relative to the integration base URL.' },
					{ name: 'category', type: 'string', desc: 'Grouping in the picker.' },
					{ name: 'read_only', type: 'boolean', desc: 'Whether it mutates. Drives confirmation and retry behaviour.' },
					{ name: 'enabled', type: 'boolean', desc: 'Hide an operation you do not want exposed without deleting it.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/integrations', role: 'Viewer', desc: 'List with health and counts.' },
					{ method: 'GET', path: '/api/integrations/{id}', role: 'Viewer', desc: 'One integration.' },
					{ method: 'POST', path: '/api/integrations', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/integrations/{id}', role: 'Admin', desc: 'Update. Omitting `auth_config` leaves stored credentials alone.' },
					{ method: 'DELETE', path: '/api/integrations/{id}', role: 'Admin', desc: 'Soft delete.' },
					{ method: 'POST', path: '/api/integrations/{id}/check', role: 'Operator', desc: 'Probe health now and record the result.' },
					{ method: 'POST', path: '/api/integrations/{id}/sync-actions', role: 'Admin', desc: 'Rebuild the action catalogue from the linked specs.' },
					{ method: 'GET', path: '/api/integrations/{id}/actions', role: 'Viewer', desc: 'The catalogue.' },
					{ method: 'PUT', path: '/api/integrations/{id}/actions/{actionId}', role: 'Admin', desc: 'Edit an action — rename, recategorise, enable or disable.' }
				]
			}
		]
	},
	{
		slug: 'mcp',
		title: 'MCP servers',
		group: 'Integrations & AI',
		module: 'integrations',
		tagline: 'Nashira as an MCP client: connect a tool server, sync its catalogue, call its tools.',
		uiPath: '/admin/mcp',
		apiBase: '/api/mcp/servers',
		role: 'Viewer',
		purpose:
			'Model Context Protocol servers expose tools. Register one and its tools become available to the agent and to workflows through the `mcp_call` snippet — without writing an adapter for each.',
		keywords: 'model context protocol tools client streamable http oauth sync catalog',
		blocks: [
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Client, not server',
				text: 'Nashira connects **out** to MCP servers. It does not expose its own tools over MCP — those are REST endpoints. That decision is unchanged; only the client side is implemented.'
			},
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Display name.' },
					{ name: 'url', type: 'string', required: true, desc: 'Streamable HTTP endpoint of the server.' },
					{ name: 'transport', type: 'string', default: 'http', desc: 'Read-only today.' },
					{ name: 'auth_type', type: 'string', default: 'none', desc: '`none`, `api_key`, `bearer`, `basic`, `headers`, `oauth_client_credentials`, `oauth_authorization_code` (browser consent via the Authorize button; PKCE, endpoint discovery and dynamic client registration are handled by the backend).' },
					{ name: 'auth_config', type: 'object', desc: '**Write-only**, encrypted at rest. Shape depends on `auth_type`.' },
					{ name: 'clear_auth_config', type: 'boolean', desc: 'Update only. Explicit removal — an omitted `auth_config` means "unchanged", since the UI cannot echo a secret back to resubmit it.' },
					{ name: 'headers', type: 'string | null', desc: 'Extra headers on every call.' },
					{ name: 'tls_skip_verify', type: 'boolean', default: 'false', desc: 'Accept an untrusted certificate. Internal servers only.' },
					{ name: 'allow_private_network', type: 'boolean', desc: 'Permit an internal address.' },
					{ name: 'enabled', type: 'boolean', default: 'true', desc: 'Suspend without deleting.' },
					{ name: 'status / last_check_error / last_checked_at', type: 'various', desc: 'Read-only health.' },
					{ name: 'tool_count / tools_synced_at', type: 'various', desc: 'Read-only catalogue state.' }
				]
			},
			{
				kind: 'params',
				title: 'Tool fields',
				rows: [
					{ name: 'name / title / description', type: 'string', desc: 'As advertised by the server.' },
					{ name: 'input_schema', type: 'object', desc: 'JSON Schema for the arguments — drives both the agent and the snippet form.' },
					{ name: 'enabled', type: 'boolean', desc: 'Expose this tool or not.' },
					{ name: 'disappeared_at', type: 'timestamp | null', desc: 'Set when a sync no longer finds the tool. It is kept, not deleted, so a workflow referencing it fails with an explanation instead of a missing id.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/mcp/servers', role: 'Viewer', desc: 'List servers.' },
					{ method: 'POST', path: '/api/mcp/servers', role: 'Admin', desc: 'Register.' },
					{ method: 'PUT', path: '/api/mcp/servers/{id}', role: 'Admin', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/mcp/servers/{id}', role: 'Admin', desc: 'Soft delete.' },
					{ method: 'POST', path: '/api/mcp/servers/{id}/check', role: 'Operator', desc: 'Health probe.' },
					{ method: 'POST', path: '/api/mcp/servers/{id}/sync', role: 'Admin', desc: 'Refresh the tool catalogue.' },
					{ method: 'GET', path: '/api/mcp/servers/{id}/tools', role: 'Viewer', desc: 'Tools with their schemas.' },
					{ method: 'PUT', path: '/api/mcp/servers/{id}/tools/{toolId}', role: 'Admin', desc: 'Enable or disable one tool.' },
					{ method: 'POST', path: '/api/mcp/servers/{id}/call', role: 'Operator', desc: 'Invoke a tool directly — useful for testing before wiring it into a workflow.' }
				]
			}
		]
	},
	{
		slug: 'api-specs',
		title: 'API specs',
		group: 'Integrations & AI',
		module: 'ai-studio',
		tagline: 'OpenAPI documents that teach the agent how to call an API.',
		uiPath: '/admin/specs',
		apiBase: '/api/ai/specs',
		role: 'Admin',
		purpose:
			'Upload an OpenAPI document and its operations become callable — by the agent through discover/execute, and by workflows through the integration action catalogue. One upload, both consumers.',
		keywords: 'openapi swagger yaml json operations dynamic api discover execute ssrf',
		blocks: [
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'api', type: 'string', required: true, desc: 'Short name for the spec. Fixed after creation.' },
					{ name: 'content', type: 'string', required: true, desc: 'The OpenAPI document, JSON or YAML. Returned only by the detail endpoint.' },
					{ name: 'base_url', type: 'string | null', desc: 'Overrides the spec\'s own `servers` and the linked integration\'s URL.' },
			{ name: 'auth_type', type: 'string', default: 'none', desc: '`none`, `token`, `bearer`, `basic`, `header`. Linked specs keep this as `none` to inherit the integration\'s credentials.' },
					{ name: 'verify_ssl', type: 'boolean', default: 'true', desc: 'Certificate validation for calls made through this spec.' },
					{ name: 'allow_private_network', type: 'boolean', default: 'true', desc: 'SSRF guard override — see below.' },
					{ name: 'integration_id', type: 'uuid | null', desc: 'Link to an integration to inherit its URL and credentials. Null = a self-contained, reusable spec.' },
					{ name: 'clear_integration', type: 'boolean', desc: 'Update only. Unlink, since an empty id on a partial update means "unchanged".' },
					{ name: 'operation_count', type: 'number', desc: 'Read-only. Operations parsed out of the document.' }
				]
			},
			{
				kind: 'note',
				tone: 'warning',
				title: 'Which SSRF flag applies',
				text: 'If the spec carries its own `base_url`, the **spec\'s** `allow_private_network` governs. If it is blank and the spec inherits an integration\'s URL, the **integration\'s** flag governs. The default is `true` because on-prem network equipment on RFC-1918 is the normal case — turn it off for a spec that should only ever reach the public internet.'
			},
			{
				kind: 'prose',
				text: 'Nashira ships built-in specs describing its own API, so the agent can drive the console through the same surface a person uses.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/ai/specs', role: 'Admin', desc: 'List, without the document bodies.' },
					{ method: 'GET', path: '/api/ai/specs/{id}', role: 'Admin', desc: 'One spec including `content`.' },
					{ method: 'POST', path: '/api/ai/specs', role: 'Admin', desc: 'Create — the document is parsed and its operations indexed.' },
					{ method: 'PUT', path: '/api/ai/specs/{id}', role: 'Admin', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/ai/specs/{id}', role: 'Admin', desc: 'Delete the spec and its operations.' }
				]
			}
		]
	},
	{
		slug: 'prompt-skills',
		title: 'Prompt skills',
		group: 'Integrations & AI',
		module: 'ai-studio',
		tagline: 'Operational knowledge that is always in the agent\'s context.',
		uiPath: '/admin/skills',
		apiBase: '/api/skills',
		role: 'Admin',
		purpose:
			'A skill is how the agent should behave and what it should know before it starts — conventions, escalation rules, house style. Unlike knowledge articles, a global skill is not looked up; it is present in every turn. A skill tied to an integration is indexed in every turn and loaded in full only when that integration is in play.',
		keywords: 'system prompt persona instructions base.md knowledge behaviour priority',
		blocks: [
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Identifies the skill; profiles reference it by name.' },
					{ name: 'content', type: 'string', required: true, desc: 'The prompt text itself.' },
					{ name: 'priority', type: 'number', desc: 'Assembly order. **Lower is included first** — check the field help before assuming.' },
					{ name: 'integration_id', type: 'uuid | null', desc: 'Ties the skill to one integration, so knowledge about a system travels with it — and stays out of the prompt until that system is in play: the agent always sees a one-line index of these skills, and the full text is loaded when the user names the integration, when a call goes to its API, or when the agent asks for it with `load_skill`. Once loaded it stays loaded for that conversation.' },
					{ name: 'created_by', type: 'uuid | null', desc: 'Read-only author.' },
					{ name: 'is_active', type: 'boolean', desc: 'Deactivate without deleting.' }
				]
			},
			{
				kind: 'prose',
				text: 'Built-in skills ship with the product and can be edited in place; user skills are additive. Which skills a given user gets is decided by their **profile**. Prefer tying a skill that documents one system (its API quirks, pagination, safety rules) to that integration: a global skill costs its full length on every turn about anything, an integration skill only when it is needed.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/skills', role: 'Admin', desc: 'List user skills.' },
					{ method: 'GET', path: '/api/skills/builtin', role: 'Admin', desc: 'The shipped skills.' },
					{ method: 'PUT', path: '/api/skills/builtin/{name}', role: 'Admin', desc: 'Edit a built-in skill.' },
					{ method: 'GET', path: '/api/skills/{id}', role: 'Admin', desc: 'One skill.' },
					{ method: 'POST', path: '/api/skills', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/skills/{id}', role: 'Admin', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/skills/{id}', role: 'Admin', desc: 'Delete.' }
				]
			}
		]
	},
	{
		slug: 'ai-providers',
		title: 'AI providers',
		group: 'Integrations & AI',
		module: 'ai-studio',
		tagline: 'Which model answers, and where it runs.',
		uiPath: '/admin/providers',
		apiBase: '/api/ai/providers',
		role: 'Admin',
		purpose:
			'Inference is provider-agnostic. Configure a commercial API, a local model, or nothing at all in an air-gapped deployment — the rest of Nashira does not change.',
		keywords: 'llm openai anthropic gemini deepseek kimi moonshot ollama custom model inference sovereignty local',
		blocks: [
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'How the provider appears in pickers.' },
					{ name: 'type', type: 'string', required: true, desc: 'Which client to use — `openai`, `anthropic`, `gemini`, `deepseek`, `kimi`, `ollama`, or `custom` for any other OpenAI-compatible endpoint.' },
					{ name: 'base_url', type: 'string | null', desc: 'Override the default endpoint. Optional for every named type; **required** for `custom`, which has no vendor endpoint to fall back to.' },
					{ name: 'default_model', type: 'string', required: true, desc: 'Model id used when a request does not name one.' },
					{ name: 'api_key', type: 'string', desc: '**Write-only.** Responses expose `has_api_key` instead.' },
					{ name: 'enabled', type: 'boolean', default: 'true', desc: 'Disable without deleting.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Where the model runs is separate from where the audit lives',
				text: 'Even with a cloud model, the decision record — workflows, runs, approvals, the audit chain — stays in your database. Moving to a local model changes inference, not governance.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/ai/providers', role: 'Admin', desc: 'List.' },
					{ method: 'POST', path: '/api/ai/providers', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/ai/providers/{id}', role: 'Admin', desc: 'Update. Omit `api_key` to keep the stored one.' },
					{ method: 'DELETE', path: '/api/ai/providers/{id}', role: 'Admin', desc: 'Delete.' }
				]
			}
		]
	},
	{
		slug: 'notifications',
		title: 'Notification channels',
		group: 'Integrations & AI',
		module: 'communications',
		tagline: 'Outbound notifications to Slack, Teams or any webhook — with every attempt recorded.',
		uiPath: '/admin/notifications',
		apiBase: '/api/notifications/channels',
		role: 'Admin',
		purpose:
			'Tell people what happened. "Did the on-call channel actually get the alert?" is a question asked afterwards, when the log has already rotated — so every delivery attempt is persisted.',
		keywords: 'slack teams webhook notifications alerts delivery history outbound',
		blocks: [
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Display name; also generates the slug.' },
					{ name: 'kind', type: 'string', required: true, desc: '`slack`, `teams` or `webhook` — decides the payload shape.' },
					{ name: 'webhook_url', type: 'string', desc: '**Write-only**, encrypted at rest. The URL is the credential for an incoming webhook, so it is treated as one.' },
					{ name: 'headers', type: 'string | null', desc: 'Extra headers for a generic webhook.' },
					{ name: 'allow_private_network', type: 'boolean', desc: 'Permit an internal receiver.' },
					{ name: 'enabled', type: 'boolean', default: 'true', desc: 'Silence a channel without losing it.' },
					{ name: 'target_host', type: 'string | null', desc: 'Read-only. Host extracted from the URL, so you can tell channels apart without exposing the secret.' },
					{ name: 'has_webhook_url', type: 'boolean', desc: 'Read-only. Whether a URL is stored.' },
					{ name: 'status / last_check_error / last_checked_at', type: 'various', desc: 'Read-only health.' }
				]
			},
			{
				kind: 'params',
				title: 'Delivery record',
				rows: [
					{ name: 'preview', type: 'string', desc: 'Beginning of the message, for identifying it later.' },
					{ name: 'success / status_code / error', type: 'various', desc: 'Outcome, including the receiver\'s error body, truncated.' },
					{ name: 'attempts', type: 'number', desc: 'How many were actually made — a send that broke on the first 400 records 1, not 3.' },
					{ name: 'elapsed_ms', type: 'number', desc: 'Wall clock for the whole send.' },
					{ name: 'workflow_run_id', type: 'uuid | null', desc: 'The run that triggered it, when applicable.' }
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/notifications/channels', role: 'Admin', desc: 'List.' },
					{ method: 'POST', path: '/api/notifications/channels', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/notifications/channels/{id}', role: 'Admin', desc: 'Update. Omit the URL to keep the stored one.' },
					{ method: 'DELETE', path: '/api/notifications/channels/{id}', role: 'Admin', desc: 'Soft delete.' },
					{ method: 'POST', path: '/api/notifications/channels/{id}/check', role: 'Admin', desc: 'Reachability probe.' },
					{ method: 'POST', path: '/api/notifications/channels/{id}/send', role: 'Admin', desc: 'Post a real message — the only honest test.' },
					{ method: 'GET', path: '/api/notifications/channels/{id}/deliveries', role: 'Admin', desc: 'Delivery history.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Outbound only',
				text: 'Messaging channels send; they do not receive. Inbound messaging — replying from Slack, identity linking, per-vendor signature verification — is a separate feature and is deliberately not implemented. (Email is different: an email channel with an IMAP host does have an inbound side — see Email channels.)'
			}
		]
	},
	{
		slug: 'email',
		title: 'Email channels',
		group: 'Integrations & AI',
		module: 'communications',
		tagline: 'Named SMTP relays — and, with IMAP configured, full mailboxes the assistant and workflows can manage.',
		uiPath: '/admin/email',
		apiBase: '/api/email/channels',
		role: 'Admin',
		purpose:
			'The relay in `appsettings` remains the deployment default. A channel is a per-workflow override — a different sender for change notifications than for reports, without an application restart. Adding an IMAP host to a channel opens the inbound side: the assistant (list_emails, read_email, archive_email, …) and the email_mailbox workflow snippet can then read, mark, move, archive and delete mail on that account.',
		keywords: 'smtp imap relay starttls ssl 465 587 993 mail sender mailbox inbox notifications',
		blocks: [
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'name', type: 'string', required: true, desc: 'Display name; also generates the slug.' },
					{ name: 'host', type: 'string', required: true, desc: 'SMTP host.' },
					{ name: 'port', type: 'number', default: '587', desc: '1–65535. The form suggests 465 when you pick implicit TLS.' },
					{ name: 'security', type: 'string', default: 'starttls', desc: 'See the modes below.' },
					{ name: 'username', type: 'string | null', desc: 'Blank for an unauthenticated relay.' },
					{ name: 'password', type: 'string', desc: '**Write-only**, encrypted at rest. Omit on update to keep the stored value.' },
					{ name: 'clear_password', type: 'boolean', desc: 'Update only. Explicit removal.' },
					{ name: 'from_address', type: 'string', required: true, desc: 'Envelope sender.' },
					{ name: 'from_name', type: 'string | null', desc: 'Display name on the From header.' },
					{ name: 'default_recipients', type: 'string | null', desc: 'Comma-separated; used when a send names nobody.' },
					{ name: 'enabled', type: 'boolean', default: 'true', desc: 'A disabled channel refuses sends with a clear message.' },
					{ name: 'has_password', type: 'boolean', desc: 'Read-only.' },
					{ name: 'imap_host', type: 'string | null', desc: 'Optional. Set it to open the inbound (mailbox) side; blank = outbound-only. On update, an explicit empty string clears it.' },
					{ name: 'imap_port', type: 'number', default: '993', desc: '1–65535. The form suggests 143 when you pick STARTTLS.' },
					{ name: 'imap_security', type: 'string', default: 'ssl', desc: 'Same modes as SMTP; IMAP convention is implicit TLS on 993.' },
					{ name: 'imap_username', type: 'string | null', desc: 'Blank to reuse the SMTP username.' },
					{ name: 'imap_password', type: 'string', desc: '**Write-only**, encrypted at rest. Blank to reuse the SMTP password; omit on update to keep the stored value.' },
					{ name: 'clear_imap_password', type: 'boolean', desc: 'Update only. Explicit removal.' },
					{ name: 'has_imap_password', type: 'boolean', desc: 'Read-only.' },
					{ name: 'imap_configured', type: 'boolean', desc: 'Read-only — true when an IMAP host is set.' }
				]
			},
			{
				kind: 'values',
				title: 'Security modes',
				rows: [
					{ value: 'starttls', desc: 'Port 587. **Strict** — if the relay cannot offer TLS the send fails rather than silently dropping to plaintext.' },
					{ value: 'ssl', desc: 'Port 465, implicit TLS from the first byte.' },
					{ value: 'none', desc: 'Plaintext. Credentials and message bodies cross the network unencrypted — only for a relay on a trusted segment.' }
				]
			},
			{
				kind: 'note',
				tone: 'neutral',
				title: 'Two mail paths, on purpose',
				text: 'Channels use MailKit, which expresses all three modes honestly — including implicit TLS on 465. The configuration-based default path uses System.Net.Mail, whose `EnableSsl` actually means STARTTLS, so it does not offer an "ssl" option rather than offering one that does something else.'
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/email/channels', role: 'Admin', desc: 'List.' },
					{ method: 'GET', path: '/api/email/channels/{id}', role: 'Admin', desc: 'One channel.' },
					{ method: 'POST', path: '/api/email/channels', role: 'Admin', desc: 'Create.' },
					{ method: 'PUT', path: '/api/email/channels/{id}', role: 'Admin', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/email/channels/{id}', role: 'Admin', desc: 'Soft delete.' },
					{ method: 'POST', path: '/api/email/channels/{id}/test', role: 'Admin', desc: 'Send a real test message. Body `{ "to": "…" }`, or blank to use the channel defaults.' },
					{ method: 'POST', path: '/api/email/channels/{id}/test-imap', role: 'Admin', desc: 'Connect to the mailbox and list folders — the equivalent honest test for the IMAP side.' }
				]
			}
		]
	},
	{
		slug: 'python-modules',
		title: 'Python modules',
		group: 'Integrations & AI',
		module: 'automation',
		tagline: 'The allowlist a python_snippet may import.',
		uiPath: '/admin/python-modules',
		apiBase: '/api/python-modules',
		role: 'Admin',
		purpose:
			'Sandboxed Python is only as constrained as its imports. This is an allowlist — anything not on it cannot be imported, at authoring time or at runtime.',
		keywords: 'sandbox allowlist import security stdlib script python',
		blocks: [
			{
				kind: 'params',
				title: 'Fields',
				rows: [
					{ name: 'module', type: 'string', required: true, desc: 'Import name, e.g. `json`, `ipaddress`, `re`.' },
					{ name: 'description', type: 'string | null', desc: 'Why it is allowed.' },
					{ name: 'requires_network', type: 'boolean', default: 'false', desc: 'Marks a module that can reach the network. Usable only by a snippet with `network_enabled`.' },
					{ name: 'enabled', type: 'boolean', default: 'true', desc: 'Revoke a module without deleting the record.' }
				]
			},
			{
				kind: 'prose',
				text: 'Eighteen purely computational standard-library modules are seeded. **None is network-capable**, deliberately: opting into network access should mean an administrator added a network module knowingly, not that one arrived with the defaults.'
			},
			{
				kind: 'list',
				items: [
					'Authoring-time scan: `__import__`, `eval`, `exec` and bare `importlib` are rejected with an explanation.',
					'Runtime enforcement: an import hook inside the interpreter applies the allowlist, so a dynamic import cannot slip past the scanner.',
					'Environment scrubbing: the child process gets a small allowlist of variables. This is the one control that survives an interpreter escape.'
				]
			},
			{
				kind: 'endpoints',
				rows: [
					{ method: 'GET', path: '/api/python-modules', role: 'Admin', desc: 'List.' },
					{ method: 'POST', path: '/api/python-modules', role: 'Admin', desc: 'Allow a module.' },
					{ method: 'PUT', path: '/api/python-modules/{id}', role: 'Admin', desc: 'Update.' },
					{ method: 'DELETE', path: '/api/python-modules/{id}', role: 'Admin', desc: 'Revoke.' }
				]
			}
		]
	}
];
