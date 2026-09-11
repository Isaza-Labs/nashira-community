import { expect, test } from '@playwright/test';

import { mockManifest } from './deployment';

// Visual capture helper (not an assertion). Seeds a fake admin session + theme
// prefs into localStorage so authed pages render without a backend, then shots
// the admin hub in both modes and the kitchen sink as an overall-polish gauge.
// Run: npx playwright test shot --project=chromium (or default).

const session = {
	accessToken: 'dev.fake.token',
	refreshToken: 'dev.fake.refresh',
	userId: '00000000-0000-0000-0000-000000000001',
	username: 'admin',
	role: 'admin',
	expiresAt: 4102444800000 // year 2100 — never "expiring soon", no refresh fires
};

function seed(mode: 'light' | 'dark') {
	const s = JSON.stringify(session);
	const p = JSON.stringify({ mode });
	return `try {
		localStorage.setItem('nashira:auth', ${JSON.stringify(s)});
		localStorage.setItem('nashira:prefs', ${JSON.stringify(p)});
	} catch {}`;
}

test.use({ viewport: { width: 1440, height: 900 } });

// The shell asks what this deployment runs before it renders any destination. These
// captures are of a full install, so it answers with everything enabled.
test.beforeEach(({ page }) => mockManifest(page));

for (const mode of ['light', 'dark'] as const) {
	test(`admin hub — ${mode}`, async ({ page }) => {
		await page.addInitScript(seed(mode));
		await page.goto('/admin');
		await page.getByRole('heading', { name: 'Admin', level: 1 }).waitFor();
		await page.waitForTimeout(400);
		await page.screenshot({ path: `e2e/shots/admin-${mode}.png`, fullPage: true });
	});
}

test('kitchen sink — light', async ({ page }) => {
	await page.addInitScript(seed('light'));
	await page.goto('/kitchen-sink');
	await page.getByRole('heading', { name: 'Kitchen sink', level: 1 }).waitFor();
	await page.waitForTimeout(400);
	await page.screenshot({ path: 'e2e/shots/kitchen-light.png', fullPage: true });
});

test('account — light', async ({ page }) => {
	await page.addInitScript(seed('light'));
	await page.goto('/account');
	await page.getByRole('heading', { name: 'Account', level: 1 }).waitFor();
	await page.waitForTimeout(400);
	await page.screenshot({ path: 'e2e/shots/account-light.png', fullPage: true });
});

test('chat — light', async ({ page }) => {
	await page.addInitScript(seed('light'));
	await page.goto('/chat');
	await page.getByRole('heading', { name: 'How can I help?' }).waitFor();
	await page.waitForTimeout(400);
	await page.screenshot({ path: 'e2e/shots/chat-light.png', fullPage: false });
});

test('login — dark', async ({ page }) => {
	await page.addInitScript(`try { localStorage.setItem('nashira:prefs', '{"mode":"dark"}'); } catch {}`);
	await page.goto('/login');
	await page.getByRole('button', { name: 'Sign in' }).waitFor();
	await page.waitForTimeout(400);
	await page.screenshot({ path: 'e2e/shots/login-dark.png', fullPage: false });
});

test('prompt skills — built-in + tenant rows', async ({ page }) => {
	await page.addInitScript(seed('light'));
	await page.route('**/api/skills/builtin', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify([
				{
					name: 'base.md',
					content:
						'# Nashira — base system prompt\n\nYou are **Nashira**, a network automation assistant.\n\n## Tool errors\n\n- Follow `_correction_hint` before retrying.\n- Do not retry the same call twice.\n\nTools:\n{tool_list}\n'
				}
			])
		})
	);
	await page.route('**/api/skills?**', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({
				total: 1,
				limit: 100,
				offset: 0,
				items: [
					{
						ai_prompt_skill_id: 's1',
						name: 'cisco-style',
						content: 'Prefer IOS-XE syntax in examples.',
						priority: 10,
						is_active: true,
						created_at: '2026-01-01T00:00:00Z',
						updated_at: '2026-01-01T00:00:00Z'
					}
				]
			})
		})
	);
	await page.goto('/admin/skills');
	await page.getByText('built-in').waitFor();
	await page.waitForTimeout(300);
	await page.screenshot({ path: 'e2e/shots/skills-light.png', fullPage: false });

	// Open the built-in editor: highlighted markdown, name fixed, save enabled.
	await page.getByRole('button', { name: 'Edit built-in skill' }).click();
	await page.waitForTimeout(300);
	await page.screenshot({ path: 'e2e/shots/skills-builtin-editor.png', fullPage: false });
	await page.getByRole('button', { name: 'Cancel' }).click();

	// "Load file" reads a local .md into the editor and names the skill from the
	// filename — the upload path that replaces copy-pasting by hand.
	await page.getByRole('button', { name: 'New skill' }).click();
	await page.setInputFiles('input[type="file"]', {
		name: 'vlan-troubleshooting.md',
		mimeType: 'text/markdown',
		buffer: Buffer.from('# VLAN troubleshooting\n\n- Check `show vlan brief` first.\n')
	});
	await expect(page.getByLabel('Name')).toHaveValue('vlan-troubleshooting');
	await expect(page.getByLabel('Content')).toHaveValue(/VLAN troubleshooting/);
	await page.waitForTimeout(300);
	await page.screenshot({ path: 'e2e/shots/skills-upload.png', fullPage: false });
});

test('credentials — auth methods + oauth2 form', async ({ page }) => {
	await page.addInitScript(seed('light'));
	await page.route('**/api/credential?**', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({
				total: 3,
				limit: 100,
				offset: 0,
				items: [
					{ credential_id: 'c1', name: 'core-ssh', type: 'ssh', username: 'netops', auth_method: 'password', has_password: true, has_private_key: false, has_token: false, has_client_secret: false, api_key_header: null, client_id: null, token_url: null, scopes: null, created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' },
					{ credential_id: 'c2', name: 'github-pat', type: 'git_token', username: 'nashira-bot', auth_method: 'token', has_password: false, has_private_key: false, has_token: true, has_client_secret: false, api_key_header: null, client_id: null, token_url: null, scopes: null, created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' },
					{ credential_id: 'c3', name: 'idp-service', type: 'oauth', username: null, auth_method: 'oauth2', has_password: false, has_private_key: false, has_token: false, has_client_secret: true, api_key_header: null, client_id: 'svc-nashira', token_url: 'https://idp.example.com/oauth2/token', scopes: 'read:devices', created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' }
				]
			})
		})
	);
	await page.goto('/admin/credentials');
	await page.getByText('github-pat').waitFor();
	await page.waitForTimeout(300);
	await page.screenshot({ path: 'e2e/shots/credentials-light.png', fullPage: false });

	// Open the create modal and switch to OAuth2 to capture the dynamic form.
	await page.getByRole('button', { name: 'New credential' }).click();
	await page.getByLabel('Auth method').selectOption('oauth2');
	await page.waitForTimeout(300);
	await page.screenshot({ path: 'e2e/shots/credentials-oauth2-form.png', fullPage: false });
});

test('devices table — dark', async ({ page }) => {
	await page.addInitScript(seed('dark'));
	await page.route('**/api/device?**', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({
				total: 3,
				limit: 100,
				offset: 0,
				// snake_case, matching the API DTO the client maps from.
				items: [
					{ device_id: 'd1', device_name: 'core-sw-01', ip_address: '10.0.0.1', platform: 'ios-xe', vendor: 'cisco', os_version: '17.9.4', site: 'Madrid', role: 'core', status: 'active', credential_id: null, has_host_key_fingerprint: true, created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' },
					{ device_id: 'd2', device_name: 'edge-rt-04', ip_address: '10.0.2.4', platform: 'junos', vendor: 'juniper', os_version: '22.4R3', site: 'Barcelona', role: 'edge', status: 'pending', credential_id: null, has_host_key_fingerprint: false, created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' },
					{ device_id: 'd3', device_name: 'acc-sw-17', ip_address: '10.0.9.17', platform: 'nx-os', vendor: 'cisco', os_version: '10.3.5', site: 'Madrid', role: 'access', status: 'failed', credential_id: null, has_host_key_fingerprint: true, created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' }
				]
			})
		})
	);
	await page.goto('/devices');
	await page.getByRole('heading', { name: 'Devices', level: 1 }).waitFor();
	await page.waitForTimeout(400);
	await page.screenshot({ path: 'e2e/shots/devices-dark.png', fullPage: false });
});

test('permissions — selecting a user renders the matrix without crashing', async ({ page }) => {
	const errors: string[] = [];
	page.on('pageerror', (e) => errors.push(String(e)));

	await page.addInitScript(seed('light'));
	// Mock the three endpoints the page hits (browser-level, so no backend needed).
	await page.route('**/api/users**', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({
				total: 2,
				limit: 200,
				offset: 0,
				items: [
					{ user_id: 'u1', username: 'alice', email: 'a@x.io', role: 'operator', is_active: true, locked: false, profile_id: null, password_changed_at: '2026-01-01T00:00:00Z', created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' },
					{ user_id: 'u2', username: 'bob', email: 'b@x.io', role: 'viewer', is_active: true, locked: false, profile_id: null, password_changed_at: '2026-01-01T00:00:00Z', created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' }
				]
			})
		})
	);
	await page.route('**/api/permissions/domains', (route) =>
		route.fulfill({ contentType: 'application/json', body: JSON.stringify(['devices', 'inventory', 'knowledge']) })
	);
	await page.route('**/api/permissions/users/**', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify([{ tool_domain: 'devices', can_read: true, can_write: false, can_execute: false }])
		})
	);

	await page.goto('/admin/permissions');
	await page.getByRole('heading', { name: 'Permissions', level: 1 }).waitFor();
	await page.locator('select').selectOption('u1');
	// The matrix must render (Read column header) — the pre-fix bug crashed here.
	await page.getByRole('columnheader', { name: 'Read' }).waitFor();
	await page.waitForTimeout(300);
	await page.screenshot({ path: 'e2e/shots/permissions-light.png', fullPage: false });

	expect(errors, `unexpected page errors:\n${errors.join('\n')}`).toEqual([]);
});

test('overview dashboard', async ({ page }) => {
	await page.addInitScript(seed('dark'));
	await page.route('**/api/device?**', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({
				total: 4, limit: 200, offset: 0,
				items: [
					{ device_id: 'd1', device_name: 'core-sw-01', ip_address: '10.0.0.1', platform: 'ios-xe', vendor: 'cisco', os_version: '17.9', site: 'Madrid', role: 'core', status: 'active', credential_id: null, has_host_key_fingerprint: true, created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' },
					{ device_id: 'd2', device_name: 'edge-rt-04', ip_address: '10.0.2.4', platform: 'junos', vendor: 'juniper', os_version: '22.4', site: 'Barcelona', role: 'edge', status: 'active', credential_id: null, has_host_key_fingerprint: true, created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' },
					{ device_id: 'd3', device_name: 'acc-sw-17', ip_address: '10.0.9.17', platform: 'nx-os', vendor: 'cisco', os_version: '10.3', site: 'Madrid', role: 'access', status: 'failed', credential_id: null, has_host_key_fingerprint: false, created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' },
					{ device_id: 'd4', device_name: 'acc-sw-18', ip_address: '10.0.9.18', platform: 'nx-os', vendor: 'cisco', os_version: '10.3', site: 'Madrid', role: 'access', status: 'pending', credential_id: null, has_host_key_fingerprint: false, created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' }
				]
			})
		})
	);
	await page.route('**/api/inventory/sources?**', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({
				total: 2, limit: 100, offset: 0,
				items: [
					{ inventory_source_id: 's1', name: 'netbox-prod', kind: 'netbox', base_url: 'https://nb', token_secret_ref: null, site_filter: null, allow_private_network: true, last_synced_at: new Date(Date.now() - 6 * 60000).toISOString(), created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' },
					{ inventory_source_id: 's2', name: 'netbox-lab', kind: 'netbox', base_url: 'https://lab', token_secret_ref: null, site_filter: null, allow_private_network: true, last_synced_at: null, created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' }
				]
			})
		})
	);
	await page.route('**/api/workflows?**', (route) =>
		route.fulfill({ contentType: 'application/json', body: JSON.stringify({ total: 7, limit: 1, offset: 0, items: [] }) }));
	await page.route('**/api/knowledge?**', (route) =>
		route.fulfill({ contentType: 'application/json', body: JSON.stringify({ total: 23, limit: 1, offset: 0, items: [] }) }));
	await page.route('**/api/audit?**', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({
				total: 2, limit: 8, offset: 0,
				items: [
					{ audit_event_id: 'a1', sequence: 42, at: new Date(Date.now() - 120000).toISOString(), entity_type: 'agent.tool', action: 'device_connect', hash: 'abc123def456', ip: null },
					{ audit_event_id: 'a2', sequence: 41, at: new Date(Date.now() - 3600000).toISOString(), entity_type: 'agent.tool', action: 'sync_netbox_inventory', hash: 'def456abc123', ip: null }
				]
			})
		})
	);

	await page.goto('/overview');
	await page.getByRole('heading', { name: 'Overview', level: 1 }).waitFor();
	await page.waitForTimeout(500);
	await page.screenshot({ path: 'e2e/shots/overview-dark.png', fullPage: false });
});

test('command palette opens with Cmd+K', async ({ page }) => {
	await page.addInitScript(seed('dark'));
	await page.goto('/account');
	await page.getByRole('heading', { name: 'Account', level: 1 }).waitFor();
	await page.keyboard.press('ControlOrMeta+k');
	await page.getByRole('dialog', { name: 'Command palette' }).waitFor();
	await page.getByLabel('Jump to').fill('cred');
	await page.waitForTimeout(250);
	await page.screenshot({ path: 'e2e/shots/command-palette.png', fullPage: false });
});
