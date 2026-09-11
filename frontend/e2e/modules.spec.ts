import { expect, test, type Page } from '@playwright/test';

import { mockManifest } from './deployment';

// The shell against a deployment that runs less than everything. No backend: the
// manifest, the identity check and the navigation permissions are mocked, which is
// exactly the point — what is asserted here is what the SPA does with the answer.
//
// The claim worth testing is not that a page looks blocked. It is that nothing of it
// runs: a route the deployment cannot serve must not mount its page, and therefore
// must not fire the requests that page makes on load. A guard that renders a notice
// over a component that already asked the API for devices has not blocked anything.

const session = {
	accessToken: 'dev.fake.token',
	refreshToken: 'dev.fake.refresh',
	userId: '00000000-0000-0000-0000-000000000001',
	username: 'admin',
	role: 'admin',
	expiresAt: 4102444800000
};

function seed() {
	return `try {
		localStorage.setItem('nashira:auth', ${JSON.stringify(JSON.stringify(session))});
	} catch {}`;
}

// Everything the shell itself asks for on load, so the only unmocked calls left are
// the ones a page would make — which is what the watcher below is looking for.
async function mockShell(page: Page, enabled: string[]) {
	await page.addInitScript(seed());

	await mockManifest(page, enabled);
	await page.route('**/api/navigation-permissions/me', (route) =>
		route.fulfill({ contentType: 'application/json', body: '[]' })
	);
	await page.route('**/api/auth/me', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({ username: session.username, role: session.role })
		})
	);
}

// Every API call the app makes, minus the three the shell needs to boot.
function watchPageRequests(page: Page): string[] {
	const seen: string[] = [];
	page.on('request', (request) => {
		const url = new URL(request.url());
		if (!url.pathname.startsWith('/api/')) return;
		if (
			url.pathname === '/api/modules' ||
			url.pathname === '/api/navigation-permissions/me' ||
			url.pathname === '/api/auth/me'
		)
			return;
		seen.push(url.pathname);
	});
	return seen;
}

test('a disabled module says so, and its page never runs', async ({ page }) => {
	await mockShell(page, ['chat', 'ai-studio', 'integrations']);
	const requests = watchPageRequests(page);

	await page.goto('/workflows');

	await expect(
		page.getByRole('heading', { name: 'Module unavailable for this deployment' })
	).toBeVisible();
	// Not the permission message: no permission can grant a capability that this
	// deployment does not run, and sending an admin to look for one wastes their time.
	await expect(page.getByText('Your navigation permissions')).toHaveCount(0);

	expect(requests).toEqual([]);
});

test('the public link handoff joins the module guard after login', async ({ page }) => {
	await mockShell(page, ['chat', 'ai-studio', 'integrations']);
	const requests = watchPageRequests(page);

	await page.goto('/link?token=one-time-token');

	await expect(
		page.getByRole('heading', { name: 'Module unavailable for this deployment' })
	).toBeVisible();
	expect(requests).toEqual([]);
});

test('the link handoff still runs when communications is enabled', async ({ page }) => {
	await mockShell(page, ['chat', 'ai-studio', 'integrations', 'communications']);
	await page.route('**/api/messaging/link/one-time-token', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({
				provider: 'telegram',
				channel_name: 'Operations',
				external_user_id: 'external-user',
				expires_at: '2099-01-01T00:00:00Z'
			})
		})
	);

	await page.goto('/link?token=one-time-token');

	await expect(page.getByRole('heading', { name: 'Link your messaging account' })).toBeVisible();
	await expect(page.getByText('Operations')).toBeVisible();
});

test('an enabled module renders its page', async ({ page }) => {
	await mockShell(page, ['chat', 'ai-studio', 'integrations', 'automation']);

	await page.goto('/workflows');

	await expect(
		page.getByRole('heading', { name: 'Module unavailable for this deployment' })
	).toHaveCount(0);
});

test('the sidebar offers only what the deployment runs', async ({ page }) => {
	await mockShell(page, ['chat', 'ai-studio', 'integrations']);

	await page.goto('/overview');
	const sidebar = page.getByLabel('Sidebar');

	await expect(sidebar.getByRole('link', { name: 'Chat' })).toBeVisible();
	await expect(sidebar.getByRole('link', { name: 'Overview' })).toBeVisible();
	await expect(sidebar.getByRole('link', { name: 'Workflows' })).toHaveCount(0);
	await expect(sidebar.getByRole('link', { name: 'Devices' })).toHaveCount(0);
	await expect(sidebar.getByRole('link', { name: 'Knowledge' })).toHaveCount(0);
});

// The palette is a second way into every destination, and a page hidden from the
// sidebar but reachable from Cmd+K is not hidden at all.
test('the command palette offers only what the deployment runs', async ({ page }) => {
	await mockShell(page, ['chat', 'ai-studio', 'integrations']);

	await page.goto('/overview');
	// The palette listens on the window, so something in the page has to have focus.
	await page.locator('main').click({ position: { x: 5, y: 5 } });
	await page.keyboard.press('ControlOrMeta+k');

	const palette = page.getByRole('dialog');
	await expect(palette).toBeVisible();
	await palette.getByRole('textbox').fill('workflows');
	await expect(palette.getByText('Workflows', { exact: true })).toHaveCount(0);
});

// Fail closed: when the manifest cannot be read the shell shows core and says why,
// rather than assuming every capability is present and filling the menu with pages
// that answer 503.
test('an unreadable manifest falls back to core and offers a retry', async ({ page }) => {
	await page.addInitScript(seed());
	await page.route('**/api/modules', (route) => route.fulfill({ status: 500, body: '{}' }));
	await page.route('**/api/navigation-permissions/me', (route) =>
		route.fulfill({ contentType: 'application/json', body: '[]' })
	);
	await page.route('**/api/auth/me', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({ username: session.username, role: session.role })
		})
	);

	await page.goto('/overview');

	// Scoped to the banner: the page underneath has its own retry for its own failed
	// request, and the recoverable state being asserted here is the shell's.
	const banner = page.getByRole('alert').filter({ hasText: 'Showing core features only' });
	await expect(banner).toBeVisible();
	await expect(banner.getByRole('button', { name: 'Retry' })).toBeVisible();

	const sidebar = page.getByLabel('Sidebar');
	await expect(sidebar.getByRole('link', { name: 'Overview' })).toBeVisible();
	await expect(sidebar.getByRole('link', { name: 'Chat' })).toHaveCount(0);
	await expect(sidebar.getByRole('link', { name: 'Workflows' })).toHaveCount(0);
});

// The entrance. Chat is home where the deployment runs it; where it does not, there is
// no conversation to land in and Overview — which is core — takes over.
test('the root lands on chat, or on overview without it', async ({ page }) => {
	await mockShell(page, ['chat', 'ai-studio', 'integrations']);
	await page.goto('/');
	await expect(page).toHaveURL(/\/chat$/);

	await mockShell(page, []);
	await page.goto('/');
	await expect(page).toHaveURL(/\/overview$/);
});

// Overview is core, so it opens everywhere — including in a deployment that runs none
// of what it summarises. What must not happen is four zeros, an error, or a request.
test('overview asks only for the capabilities the deployment runs', async ({ page }) => {
	await mockShell(page, []);
	const requests = watchPageRequests(page);

	await page.goto('/overview');
	const main = page.getByRole('main');
	await expect(main.getByRole('heading', { name: 'Overview', level: 1 })).toBeVisible();
	await expect(main.getByText('Nothing to summarise yet')).toBeVisible();
	await expect(main.getByText('Devices', { exact: true })).toHaveCount(0);
	await expect(main.getByText('Workflows', { exact: true })).toHaveCount(0);

	expect(requests).toEqual([]);
});

test('overview shows the cards of the capabilities that are on', async ({ page }) => {
	await mockShell(page, ['fleet']);
	const requests = watchPageRequests(page);

	// Fleet's own endpoints answer; everything else must simply never be asked.
	await page.route('**/api/device/stats', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({ total: 3, by_status: { healthy: 3 } })
		})
	);
	await page.route('**/api/inventory/sources?**', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({ total: 0, limit: 100, offset: 0, items: [] })
		})
	);

	await page.goto('/overview');
	// Scoped to the page: the sidebar links to Devices as well, and the sidebar is a
	// different claim (tested above).
	const main = page.getByRole('main');
	await expect(main.getByText('Devices', { exact: true })).toBeVisible();
	await expect(main.getByText('Workflows', { exact: true })).toHaveCount(0);

	// Fleet's own endpoints, and nothing belonging to a capability that is off.
	await expect.poll(() => requests.some((path) => path.startsWith('/api/device'))).toBe(true);
	expect(requests.filter((path) => path.startsWith('/api/workflows'))).toEqual([]);
	expect(requests.filter((path) => path.startsWith('/api/knowledge'))).toEqual([]);
	expect(requests.filter((path) => path.startsWith('/api/audit'))).toEqual([]);
});

// Creating the account is core's; delivering the password is not. The admin is told
// before saving, not after — by then they have handed out a password to chase.
test('new user warns that credentials will not be emailed', async ({ page }) => {
	await mockShell(page, []);
	await page.route('**/api/users?**', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({ total: 0, limit: 25, offset: 0, items: [] })
		})
	);

	await page.goto('/admin/users');
	await page.getByRole('button', { name: 'New user' }).click();

	await expect(page.getByText('Credentials will not be emailed')).toBeVisible();
});

// Docs is core, so it opens everywhere — and documents only what is deployed. A page
// explaining how to register a device, in an install with no fleet, reads as a missing
// feature rather than as one nobody bought.
test('docs describe only the capabilities the deployment runs', async ({ page }) => {
	await mockShell(page, ['automation']);

	await page.goto('/docs');
	// The section index, not the hand-written "start here" links above it.
	const index = page.getByLabel('Documentation');
	await expect(index.getByRole('link', { name: 'Workflows', exact: true })).toBeVisible();
	await expect(index.getByRole('link', { name: 'Devices', exact: true })).toHaveCount(0);
	await expect(index.getByRole('link', { name: 'Git repositories', exact: true })).toHaveCount(0);

	// And the section itself is not readable by URL either.
	await page.goto('/docs/devices');
	await expect(page.getByText('No such documentation page')).toBeVisible();
});

// The hub is core and always reachable — an administrator locked out of it would have
// no way back into a deployment's settings — but it offers only what is there.
test('the admin hub offers only deployed tools', async ({ page }) => {
	await mockShell(page, ['governance']);

	await page.goto('/admin');
	const main = page.getByRole('main');
	await expect(main.getByRole('heading', { name: 'Admin', level: 1 })).toBeVisible();
	// The tools survive a deployment without observability: the panels above them come
	// from the admin metrics API, and the hub is the way back into a deployment.
	await expect(main.getByRole('heading', { name: 'Tools' })).toBeVisible();
	await expect(main.getByRole('link', { name: /Tool permissions/ })).toBeVisible();
	await expect(main.getByRole('link', { name: /Devices/ })).toHaveCount(0);
	await expect(main.getByRole('link', { name: /Secrets/ })).toHaveCount(0);
	await expect(main.getByRole('link', { name: /Snippets/ })).toHaveCount(0);
});

// Navigation access administers what exists. Offering a switch for a page the
// deployment does not run would be a promise the shell refuses to keep: the module
// decision wins over every stored permission.
test('navigation access lists only deployed destinations', async ({ page }) => {
	await mockShell(page, ['governance']);
	await page.route('**/api/users?**', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify({ total: 0, limit: 100, offset: 0, items: [] })
		})
	);
	await page.route('**/api/navigation-permissions/roles/**', (route) =>
		route.fulfill({ contentType: 'application/json', body: '[]' })
	);

	await page.goto('/admin/navigation-permissions');
	const main = page.getByRole('main');
	await expect(main.getByText('Policies', { exact: true })).toBeVisible();
	await expect(main.getByText('Devices', { exact: true })).toHaveCount(0);
	await expect(main.getByText('Workflows', { exact: true })).toHaveCount(0);
});

// A stored permission cannot put back what the deployment does not run: visibility is
// module ∩ role ∩ permission, and the module is not negotiable.
test('a stored permission cannot re-enable a disabled module', async ({ page }) => {
	await mockShell(page, ['chat', 'ai-studio', 'integrations']);
	// Someone turned Workflows explicitly visible while automation was still deployed.
	await page.unroute('**/api/navigation-permissions/me');
	await page.route('**/api/navigation-permissions/me', (route) =>
		route.fulfill({
			contentType: 'application/json',
			body: JSON.stringify([{ page_key: '/workflows', visible: true }])
		})
	);

	await page.goto('/overview');
	await expect(page.getByLabel('Sidebar').getByRole('link', { name: 'Workflows' })).toHaveCount(0);

	await page.goto('/workflows');
	await expect(
		page.getByRole('heading', { name: 'Module unavailable for this deployment' })
	).toBeVisible();
});
