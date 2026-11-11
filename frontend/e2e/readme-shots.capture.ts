import { expect, test, type Page, type Route } from '@playwright/test';

import { mockManifest } from './deployment';

// Captures for the README (not assertions on the product). Everything shown is
// invented: the API is mocked, the device names and sites are made up, and
// addresses are from the documentation ranges (192.0.2.0/24, 198.51.100.0/24,
// 203.0.113.0/24). No backend is needed.
//
// The mocked answers are kept to what the backend can actually return: node types
// from the workflow schema, snippet types from the handler catalog, the rollback
// tier each handler has (ssh and slack_message are non-reversible), finished runs
// only, totals that match the rows, and one fixed clock.
//
// Not part of the default suite (the file name does not match it). Run:
//   npx playwright test -c playwright.readme.config.ts
// The captures land in e2e/shots/readme/, which is not tracked, in light and dark.
// The README shows the light ones, copied to docs/images/ without the "-light"
// suffix; replace them there when a screen changes.

const NOW = Date.parse('2026-11-10T15:00:00Z');
const ago = (minutes: number) => new Date(NOW - minutes * 60_000).toISOString();
const DAY = 60 * 24;

/** A stable, UUID-shaped id for a name, so fixtures can refer to each other. */
function id(seed: string): string {
	let h = 0x811c9dc5;
	let hex = '';
	for (let round = 0; hex.length < 32; round++) {
		for (const ch of `${seed}/${round}`) {
			h ^= ch.charCodeAt(0);
			h = Math.imul(h, 0x01000193) >>> 0;
		}
		hex += h.toString(16).padStart(8, '0');
	}
	return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-4${hex.slice(13, 16)}-a${hex.slice(17, 20)}-${hex.slice(20, 32)}`;
}

const session = {
	accessToken: 'dev.fake.token',
	refreshToken: 'dev.fake.refresh',
	userId: '00000000-0000-0000-0000-000000000001',
	username: 'admin',
	role: 'admin',
	expiresAt: 4102444800000
};
const seed = (mode: 'light' | 'dark') => `try {
	localStorage.setItem('nashira:auth', ${JSON.stringify(JSON.stringify(session))});
	localStorage.setItem('nashira:prefs', ${JSON.stringify(JSON.stringify({ mode }))});
} catch {}`;

const json = (route: Route, body: unknown) =>
	route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
/** The order the API lists devices and inventory sources in. */
const byName = <T,>(rows: T[], name: (row: T) => string) => [...rows].sort((a, b) => (name(a) < name(b) ? -1 : name(a) > name(b) ? 1 : 0));
/** One page of a list, cut by the limit and offset that were asked for. */
const paged = (route: Route, all: unknown[], fallbackLimit = 50) => {
	const q = new URL(route.request().url()).searchParams;
	const limit = Number(q.get('limit')) || fallbackLimit;
	const offset = Number(q.get('offset')) || 0;
	return json(route, { total: all.length, limit, offset, items: all.slice(offset, offset + limit) });
};

// ── fleet ────────────────────────────────────────────────────────────────────
const SOURCE = id('source/netbox-prod');
const LOGIN = id('credential/netops-ssh');
type Envs = [draft: boolean, qa: boolean, production: boolean];
let netboxId = 1000;
const device = (name: string, ip: string, vendor: string, platform: string, os: string, site: string, role: string, status: string, envs: Envs, synced = true) => ({
	device_id: id(`device/${name}`), device_name: name, ip_address: ip, platform, vendor, os_version: os, site, role, status,
	credential_id: LOGIN, source_id: synced ? SOURCE : null, external_id: synced ? String(++netboxId) : null,
	last_sync_at: synced ? ago(6) : null, properties: null, allow_draft: envs[0], allow_qa: envs[1], allow_production: envs[2],
	has_host_key_fingerprint: status === 'active', created_at: ago(40 * DAY), updated_at: ago(5 * DAY)
});
const ALL: Envs = [true, true, true];
const DEVICES = [
	device('mad-core-sw-01', '192.0.2.1', 'cisco', 'ios-xe', '17.9.4', 'Madrid', 'core', 'active', ALL),
	device('mad-core-sw-02', '192.0.2.2', 'cisco', 'ios-xe', '17.9.4', 'Madrid', 'core', 'active', ALL),
	device('bcn-edge-rt-01', '198.51.100.1', 'juniper', 'junos', '22.4R3', 'Barcelona', 'edge', 'active', ALL),
	device('bcn-edge-rt-02', '198.51.100.2', 'juniper', 'junos', '22.4R3', 'Barcelona', 'edge', 'active', ALL),
	device('mad-dc-leaf-11', '192.0.2.11', 'arista', 'eos', '4.31.2F', 'Madrid', 'leaf', 'active', ALL),
	device('mad-dc-leaf-12', '192.0.2.12', 'arista', 'eos', '4.31.2F', 'Madrid', 'leaf', 'active', ALL),
	device('mad-dc-spine-01', '192.0.2.21', 'nokia', 'srlinux', '24.7.2', 'Madrid', 'spine', 'active', ALL),
	device('bcn-acc-sw-17', '198.51.100.17', 'cisco', 'nx-os', '10.3(4a)', 'Barcelona', 'access', 'pending', [true, false, false], false),
	device('bcn-acc-sw-18', '198.51.100.18', 'cisco', 'nx-os', '10.3(4a)', 'Barcelona', 'access', 'failed', [true, false, false], false),
	// The rest of the estate: access switches at a third site.
	...Array.from({ length: 39 }, (_, i) =>
		device(`vlc-acc-sw-${String(i + 1).padStart(2, '0')}`, `203.0.113.${i + 10}`, 'cisco', 'ios-xe', '17.9.4', 'Valencia', 'access', i % 13 === 5 ? 'pending' : 'active', [true, true, i % 4 !== 3]))
];
const byStatus: Record<string, number> = {};
for (const d of DEVICES) byStatus[d.status] = (byStatus[d.status] ?? 0) + 1;

const SOURCES = [
	{ inventory_source_id: SOURCE, name: 'netbox-prod', kind: 'netbox', base_url: 'https://netbox.example.com', token_secret_ref: '${secret:secret:netbox-token:value}', site_filter: null, allow_private_network: true, last_synced_at: ago(6), created_at: ago(40 * DAY), updated_at: ago(6) },
	{ inventory_source_id: id('source/netbox-lab'), name: 'netbox-lab', kind: 'netbox', base_url: 'https://netbox-lab.example.com', token_secret_ref: '${secret:secret:netbox-lab-token:value}', site_filter: 'lab', allow_private_network: true, last_synced_at: ago(5 * 60), created_at: ago(12 * DAY), updated_at: ago(5 * 60) }
];

// ── workflows ────────────────────────────────────────────────────────────────
const CID = id('conversation/bgp');
const WF = (name: string, version: number, environment: string, minutes: number, nodes: number, fromChat = false) => ({
	workflow_id: id(`workflow/${name}`), name, version, environment, schema_hash: `${id(`hash/${name}`)}${id(`hash2/${name}`)}`.replace(/-/g, ''),
	last_simulation_id: id(`simulation/${name}`), conversation_id: fromChat ? CID : null, updated_at: ago(minutes), nodes
});
const WORKFLOW_ROWS = [
	WF('bgp-neighbor-health-check', 4, 'production', 35, 6, true),
	WF('interface-saturation-report', 2, 'production', 3 * 60, 4),
	WF('config-backup-to-git', 7, 'production', 22 * 60, 4),
	WF('firmware-preflight-check', 2, 'production', 28 * 60, 5),
	WF('netbox-inventory-sync', 5, 'production', 30 * 60, 4),
	WF('vlan-provisioning', 3, 'qa', 26 * 60, 5),
	WF('os-compliance-audit', 1, 'qa', 49 * 60, 4),
	WF('ntp-drift-remediation', 2, 'draft', 72 * 60, 3)
];
const WORKFLOWS = WORKFLOW_ROWS.map(({ nodes: _nodes, ...summary }) => summary);
const wf = (name: string) => WORKFLOW_ROWS.find((w) => w.name === name)!;
const BGP = wf('bgp-neighbor-health-check');

// Node `type` is the schema's (task | decision | subflow); what a node does comes
// from its snippet, whose handler type decides whether it can be rolled back.
const NODES = [
	// rest_call
	{ id: 'find_devices', snippet_id: id('snippet/inventory-query'), type: 'task', config_overrides: { method: 'GET', url: 'https://netbox.example.com/api/dcim/devices/', query: { site: '{{ input.site }}', role: '{{ input.role }}' } } },
	// ssh, run once per device, read-only
	{ id: 'collect_bgp', snippet_id: id('snippet/show-bgp-summary'), type: 'task', config_overrides: { device: '{{ device.name }}', command: 'show bgp summary', changes: false } },
	// transform
	{ id: 'parse_bgp', snippet_id: id('snippet/parse-bgp-summary'), type: 'task', config_overrides: { input: '{{ steps.collect_bgp.output }}' } },
	// transform
	{ id: 'flag_down', snippet_id: id('snippet/sessions-not-established'), type: 'decision', config_overrides: { input: '{{ steps.parse_bgp.output }}' } },
	// integration_action
	{ id: 'open_ticket', snippet_id: id('snippet/servicenow-incident'), type: 'task', config_overrides: { integration: 'servicenow', action: 'create_incident', body: { short_description: 'BGP sessions down in {{ input.site }}' } } },
	// slack_message
	{ id: 'notify_noc', snippet_id: id('snippet/noc-channel-message'), type: 'task', config_overrides: { channel: '#noc', text: 'BGP sessions down in {{ input.site }}: incident opened' } }
];
// One after another; the plan order below is this chain.
const EDGES = NODES.slice(1).map((node, i) => ({ source: NODES[i].id, target: node.id, type: 'success' }));
const SNIPPET_TYPES = ['transform', 'rest_call', 'python_snippet', 'ping', 'ssh', 'ansible_playbook', 'mcp_call', 'integration_action', 'git', 'report', 'email_send', 'email_mailbox', 'slack_message', 'netconf', 'snmp_v3'];

// ── runs ─────────────────────────────────────────────────────────────────────
// A run is stored when it ends, so every row here has finished. A failed run is
// `rolled_back` only when nothing non-reversible had changed; a step failure is
// recorded on the step, not as a run-level error.
const RUN = (n: number, name: string, status: string, final: string, changed: number, failed: number, minutes: number, seconds: number, trigger: string) => ({
	workflow_run_id: id(`run/${n}`), workflow_id: wf(name).workflow_id, workflow_name: name, environment: wf(name).environment,
	status, final_state: final, node_count: wf(name).nodes, changed_count: changed, failed_count: failed,
	started_at: ago(minutes), finished_at: ago(minutes - seconds / 60), duration_seconds: seconds, trigger, error: null, parent_run_id: null
});
const RUNS = [
	RUN(1, 'bgp-neighbor-health-check', 'completed', 'completed', 2, 0, 16, 41, 'schedule'),
	RUN(2, 'bgp-neighbor-health-check', 'completed', 'completed', 2, 0, 31, 42, 'schedule'),
	RUN(3, 'interface-saturation-report', 'completed', 'completed', 1, 0, 64, 18, 'agent'),
	RUN(4, 'netbox-inventory-sync', 'failed', 'rolled_back', 1, 1, 95, 73, 'schedule'),
	RUN(5, 'config-backup-to-git', 'completed', 'completed', 2, 0, 2 * 60, 126, 'schedule'),
	RUN(6, 'vlan-provisioning', 'completed', 'completed', 3, 0, 3 * 60, 57, 'manual'),
	RUN(7, 'bgp-neighbor-health-check', 'completed', 'completed', 0, 0, 4 * 60, 39, 'schedule'),
	RUN(8, 'os-compliance-audit', 'completed', 'completed', 0, 0, 6 * 60, 88, 'manual'),
	RUN(9, 'config-backup-to-git', 'completed', 'completed', 2, 0, 26 * 60, 131, 'schedule')
];
const DETAIL = RUNS[1];
// Steps run one after another: each starts when the one before it ended.
const STEP_PLAN: [node: string, result: string, ms: number, outputChars: number][] = [
	['find_devices', 'no_change', 240, 310],
	['collect_bgp', 'no_change', 18_400, 5120],
	['parse_bgp', 'no_change', 310, 1840],
	['flag_down', 'no_change', 40, 220],
	['open_ticket', 'changed', 1900, 410],
	['notify_noc', 'changed', 620, 96]
];
let cursor = Date.parse(DETAIL.started_at) + 150;
const STEPS = STEP_PLAN.map(([node, result, ms, out], sequence) => {
	const started = cursor;
	cursor += ms + 60;
	return {
		node_id: node, sequence, result, error_code: null, retryable: false, error: null, attempts: 1,
		started_at: new Date(started).toISOString(), finished_at: new Date(started + ms).toISOString(), duration_ms: ms,
		output_chars: out, input_chars: JSON.stringify(NODES[sequence].config_overrides).length, logs_chars: 640, child_run_id: null
	};
});

// ── overview ─────────────────────────────────────────────────────────────────
// The overview asks for agent tool calls only.
const audit = (n: number, minutes: number, action: string) => ({
	audit_event_id: id(`audit/${n}`), sequence: 4170 + n, at: ago(minutes), entity_type: 'agent.tool', action,
	hash: id(`audit-hash/${n}`).replace(/-/g, '').slice(0, 12), ip: null
});
const AUDIT = [
	audit(12, 12, 'device_connect'), audit(11, 13, 'query_devices'), audit(10, 64, 'run_workflow'), audit(9, 66, 'get_workflow'),
	audit(8, 2 * 60, 'sync_netbox_inventory'), audit(7, 3 * 60, 'search_knowledge'), audit(6, 5 * 60, 'mcp_call'), audit(5, 5 * 60 + 1, 'list_mcp_tools'),
	audit(4, 26 * 60, 'run_workflow'), audit(3, 27 * 60, 'query_devices'), audit(2, 30 * 60, 'list_workflows'), audit(1, 31 * 60, 'list_integrations')
];
const ARTICLES = ['BGP session troubleshooting', 'VLAN change checklist', 'Config backup policy', 'NetBox as source of truth', 'Maintenance windows by site',
	'Firmware upgrade runbook', 'NTP and time sources', 'Edge router naming', 'Incident severity guide', 'Escalation contacts by team', 'Spine and leaf addressing',
	'Access port standards', 'Change freeze calendar', 'SSH access policy', 'Interface description format', 'Out-of-band management', 'Syslog and retention',
	'SNMP community rotation', 'Circuit inventory', 'Peering policy', 'Route filter conventions', 'Lab environment rules', 'Post-change verification'
].map((title, i) => ({
	knowledge_article_id: id(`article/${i}`), title, slug: title.toLowerCase().replace(/[^a-z0-9]+/g, '-'), content: '', tags: ['network'],
	created_at: ago((60 - i) * DAY), updated_at: ago((30 - i) * DAY)
}));

// ── vault ────────────────────────────────────────────────────────────────────
const cred = (name: string, type: string, username: string | null, auth: string, updatedDays: number, extra = {}) => ({
	credential_id: id(`credential/${name}`), name, type, username, auth_method: auth, has_password: auth === 'password', has_private_key: auth === 'key',
	has_token: auth === 'token' || auth === 'api_key', has_client_secret: auth === 'oauth2', api_key_header: null, client_id: null,
	token_url: null, scopes: null, created_at: ago(60 * DAY), updated_at: ago(updatedDays * DAY), ...extra
});
const CREDENTIALS = [
	cred('netops-ssh', 'ssh', 'netops', 'password', 2),
	cred('automation-key', 'ssh', 'automation', 'key', 9),
	cred('config-backup-repo', 'git_token', 'nashira-bot', 'token', 14),
	cred('servicenow-api', 'servicenow', 'svc-nashira', 'api_key', 1, { api_key_header: 'X-Api-Key' }),
	cred('idp-service', 'oauth', null, 'oauth2', 21, { client_id: 'svc-nashira', token_url: 'https://idp.example.com/oauth/token' })
];
const SECRETS = [
	{ secret_id: id('secret/netbox-token'), name: 'netbox-token', description: 'NetBox API token (read-only)', has_value: true, created_by: 'admin', created_at: ago(40 * DAY), updated_at: ago(5 * DAY) },
	{ secret_id: id('secret/netbox-lab-token'), name: 'netbox-lab-token', description: 'NetBox lab API token', has_value: true, created_by: 'admin', created_at: ago(12 * DAY), updated_at: ago(12 * DAY) },
	{ secret_id: id('secret/slack-webhook'), name: 'slack-webhook', description: 'NOC channel incoming webhook', has_value: true, created_by: 'admin', created_at: ago(30 * DAY), updated_at: ago(11 * DAY) }
];

// ── chat ─────────────────────────────────────────────────────────────────────
const ASSISTANT = `I checked BGP on the two Barcelona edge routers. Here is what I found.

## Summary

One session is down and one is flapping. Nothing has been changed.

- **bcn-edge-rt-01**: neighbor \`198.51.100.254\` (AS 64512) has been **Idle** for 14 minutes
- **bcn-edge-rt-02**: neighbor \`198.51.100.253\` (AS 64513) flapped 6 times in the last hour
- All iBGP sessions to the Madrid core are established

### Neighbor detail

| Device | Neighbor | AS | State | Prefixes | Last change |
| --- | --- | --- | --- | --- | --- |
| bcn-edge-rt-01 | 198.51.100.254 | 64512 | Idle | 0 | 14 min |
| bcn-edge-rt-01 | 192.0.2.1 | 64500 | Established | 412 | 9 days |
| bcn-edge-rt-02 | 198.51.100.253 | 64513 | Established | 87 | 3 min |
| bcn-edge-rt-02 | 192.0.2.2 | 64500 | Established | 412 | 9 days |

### What I would do next

1. Open an incident for the AS 64512 session with the evidence above
2. Run the **bgp-neighbor-health-check** workflow every 15 minutes until it recovers
3. Leave the flapping session alone for now: it re-established on its own each time

> Resetting the session on \`bcn-edge-rt-01\` is a change in production and needs your confirmation.

Do you want me to open the incident and schedule the workflow?`;
const conversation = (key: string, title: string, minutes: number, createdMinutes = minutes + 8) => ({
	conversation_id: key === 'bgp' ? CID : id(`conversation/${key}`), title, status: 'active', tokens_in: 18400, tokens_out: 620,
	created_at: ago(createdMinutes), updated_at: ago(minutes)
});
const CONVERSATIONS = [
	conversation('bgp', 'Barcelona edge BGP health', 12, 21 * DAY),
	conversation('vlan', 'VLAN 240 rollout plan', 5 * 60),
	conversation('backup', 'Config backup failures', 26 * 60),
	conversation('netbox', 'NetBox sync mismatches', 50 * 60)
];

async function mockAll(page: Page) {
	// Anything not answered below fails the way an unreachable backend would, and
	// the capture then fails too (see the check after each screenshot).
	await page.route('**/api/**', (route) => route.abort());
	await mockManifest(page);
	await page.route('**/api/navigation-permissions/me', (route) => json(route, []));

	await page.route('**/api/device/stats', (route) => json(route, { total: DEVICES.length, by_status: byStatus }));
	await page.route('**/api/device?**', (route) => paged(route, byName(DEVICES, (d) => d.device_name), 100));
	await page.route('**/api/inventory/sources?**', (route) => paged(route, byName(SOURCES, (x) => x.name)));
	await page.route('**/api/knowledge?**', (route) => paged(route, ARTICLES));
	await page.route('**/api/audit?**', (route) => {
		const type = new URL(route.request().url()).searchParams.get('entityType');
		return paged(route, type ? AUDIT.filter((e) => e.entity_type === type) : AUDIT);
	});

	await page.route('**/api/workflows?**', (route) => {
		const env = new URL(route.request().url()).searchParams.get('environment');
		return paged(route, env ? WORKFLOWS.filter((w) => w.environment === env) : WORKFLOWS);
	});
	await page.route('**/api/snippets/types', (route) => json(route, SNIPPET_TYPES));
	await page.route(`**/api/workflows/${BGP.workflow_id}`, (route) =>
		json(route, {
			...WORKFLOWS[0],
			description: 'Collects the BGP summary from every edge and core device in a site, flags sessions that are not established, and opens an incident when any are down.',
			schema_version: 'v1',
			nodes: NODES,
			edges: EDGES,
			input_schema: { type: 'object', properties: { site: { type: 'string', title: 'Site' }, role: { type: 'string', title: 'Device role', default: 'edge' } }, required: ['site'] },
			change_summary: 'Notify the NOC channel after the incident is opened',
			created_at: ago(20 * DAY)
		}));
	// ssh and slack_message are non-reversible handlers; nothing an author writes lowers that.
	await page.route(`**/api/workflows/${BGP.workflow_id}/plan`, (route) =>
		json(route, { order: NODES.map((n) => n.id), rollback_reversible: false, non_reversible_nodes: ['collect_bgp', 'notify_noc'], unresolvable_nodes: [] }));

	await page.route('**/api/runs?**', (route) => paged(route, RUNS));
	await page.route(`**/api/runs/${DETAIL.workflow_run_id}`, (route) =>
		json(route, {
			...DETAIL,
			input: { site: 'Barcelona', role: 'edge' },
			target_devices: DEVICES.filter((d) => d.site === 'Barcelona' && d.role === 'edge').map((d) => d.device_id),
			steps: STEPS
		}));

	await page.route('**/api/credential?**', (route) => paged(route, CREDENTIALS, 100));
	await page.route('**/api/secrets', (route) => json(route, SECRETS));
	await page.route('**/api/git/repositories?**', (route) =>
		paged(route, [{ git_repository_id: id('repository/config-backups'), auth_credential_id: id('credential/config-backup-repo') }]));
	await page.route('**/api/integrations?**', (route) =>
		paged(route, [
			{ integration_id: id('integration/servicenow'), auth_credential_id: id('credential/servicenow-api') },
			{ integration_id: id('integration/idp'), auth_credential_id: id('credential/idp-service') }
		]));
	await page.route('**/api/mcp/servers?**', (route) => paged(route, []));

	await page.route('**/api/ai/models', (route) =>
		json(route, [{ id: 'local-model', provider: 'local', ai_provider_id: id('provider/local'), provider_type: 'custom', is_default: true }]));
	await page.route('**/api/ai/conversations?**', (route) => paged(route, CONVERSATIONS));
	await page.route(`**/api/ai/conversations/${CID}`, (route) =>
		json(route, {
			...CONVERSATIONS[0],
			messages: [
				{ role: 'user', content: 'Are any BGP sessions down on the Barcelona edge routers?' },
				{ role: 'assistant', content: ASSISTANT }
			]
		}));
}

/** Each capture, and the content that proves its data has arrived. */
const SHOTS: { name: string; path: string; height?: number; loaded: (page: Page) => Promise<unknown> }[] = [
	{ name: 'chat', path: `/chat/${CID}`, height: 900, loaded: (p) => expect(p.getByText('What I would do next')).toBeVisible() },
	{
		name: 'overview',
		path: '/overview',
		loaded: async (p) => {
			await expect(p.getByText('netbox-prod')).toBeVisible();
			await expect(p.getByText('device_connect')).toBeVisible();
			await expect(p.getByText(String(DEVICES.length), { exact: true }).first()).toBeVisible();
		}
	},
	{ name: 'workflows', path: '/workflows?env=production', loaded: (p) => expect(p.getByText('netbox-inventory-sync')).toBeVisible() },
	{
		name: 'workflow',
		path: `/workflows/${BGP.workflow_id}`,
		loaded: async (p) => {
			await expect(p.getByText('Contains non-reversible steps')).toBeVisible();
			await expect(p.getByText('notify_noc').first()).toBeVisible();
		}
	},
	{ name: 'runs', path: '/runs', loaded: (p) => expect(p.getByText('rolled back', { exact: true })).toBeVisible() },
	{
		name: 'run',
		path: `/runs/${DETAIL.workflow_run_id}`,
		loaded: async (p) => {
			await expect(p.getByText('Targeted 2 devices')).toBeVisible();
			await expect(p.getByText('notify_noc').first()).toBeVisible();
		}
	},
	{ name: 'devices', path: '/devices', loaded: (p) => expect(p.getByText('mad-core-sw-01').first()).toBeVisible() },
	{
		name: 'vault',
		path: '/admin/vault',
		loaded: async (p) => {
			await expect(p.getByText('slack-webhook').first()).toBeVisible();
			await expect(p.getByText(`${DEVICES.length} devices`)).toBeVisible();
		}
	}
];

const WIDTH = 1280;

test.use({
	// A README column is under 900 px wide, so a narrower capture keeps the text readable.
	viewport: { width: WIDTH, height: 800 },
	deviceScaleFactor: 2,
	locale: 'en-US',
	timezoneId: 'UTC',
	// The app collapses its entry animations to their final frame under reduced motion.
	contextOptions: { reducedMotion: 'reduce' }
});

for (const mode of ['light', 'dark'] as const) {
	for (const shot of SHOTS) {
		test(`${shot.name} (${mode})`, async ({ page }) => {
			const unanswered: string[] = [];
			page.on('requestfailed', (request) => {
				const path = new URL(request.url()).pathname;
				// The shell's own session check is left unanswered on purpose, as in the other specs.
				if (path.startsWith('/api/') && path !== '/api/auth/me') unanswered.push(`${request.method()} ${path}`);
			});
			if (shot.height) await page.setViewportSize({ width: WIDTH, height: shot.height });
			await page.clock.setFixedTime(NOW);
			await page.addInitScript(seed(mode));
			await mockAll(page);

			await page.goto(shot.path);
			await shot.loaded(page);
			await page.waitForLoadState('networkidle');
			await page.evaluate(() => document.fonts.ready);
			await page.screenshot({ path: `e2e/shots/readme/${shot.name}-${mode}.png`, fullPage: false, animations: 'disabled' });

			// A capture taken while a request it depends on went unanswered shows an
			// error or an empty state, not the product.
			expect(unanswered).toEqual([]);
		});
	}
}
