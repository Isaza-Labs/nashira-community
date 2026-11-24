import { expect, test, type Page, type Route } from '@playwright/test';

import { mockManifest } from './deployment';

// The Vault: one screen over the two stores the Credentials and Secrets pages used
// to show separately. No backend — every API call is answered here, and what is
// asserted is what the page sends and shows.
//
// Two kinds of claim are worth pinning down. The first is sameness: the Vault is a
// new face on unchanged APIs, so a rename must send what the Credentials page sent
// and a rotation must still ask first. The second is honesty under failure: two
// stores, several lists and a paged read can each fail or lag independently, and
// the page must never turn "unknown" into "none".

const TS = '2026-11-01T12:00:00Z';

type Cred = Record<string, unknown> & { credential_id: string; name: string; auth_method: string };

function cred(id: string, name: string, type: string, username: string | null, auth: string, extra = {}): Cred {
	const m = auth.trim().toLowerCase();
	return {
		credential_id: id,
		name,
		type,
		username,
		auth_method: auth,
		has_password: m === 'password',
		has_private_key: m === 'key',
		has_token: m === 'token' || m === 'api_key',
		has_client_secret: m === 'oauth2',
		api_key_header: null,
		client_id: null,
		token_url: null,
		scopes: null,
		created_at: TS,
		updated_at: TS,
		...extra
	};
}

// Rows as older write paths left them: the server reads the method trimmed and
// lowercased, so "KEY" is a key and " token " is a token.
const CREDS: Cred[] = [
	cred('c1', 'core-ssh', 'ssh', 'netops', 'password'),
	cred('c2', 'legacy-key', 'ssh', 'admin', 'KEY'),
	cred('c3', 'legacy-token', 'git_token', 'bot', ' token '),
	cred('c4', 'servicenow-api', 'servicenow', 'svc', 'api_key', { api_key_header: 'X-Api-Key' }),
	cred('c5', 'legacy-oauth', 'oauth', null, 'OAuth2', { client_id: 'svc-nashira', token_url: 'https://idp.example.com/t' })
];

const SECRETS = [
	{ secret_id: 's1', name: 'netbox-token', description: 'NetBox API token', has_value: true, created_by: 'admin', created_at: TS, updated_at: TS },
	{ secret_id: 's2', name: 'empty-one', description: 'never filled', has_value: false, created_by: 'admin', created_at: TS, updated_at: TS }
];

interface Options {
	role?: string;
	modules?: string[];
	creds?: Cred[];
	secrets?: typeof SECRETS;
	visibility?: { page_key: string; visible: boolean }[];
	roleRows?: { page_key: string; visible: boolean }[];
	credFail?: boolean;
	/** Credential reads fail once this many have succeeded. */
	credFailAfter?: number;
	/** The list shifts between pages: a row repeats and the total moves. */
	unstable?: boolean;
	secretFail?: boolean;
	saveFail?: boolean;
	/** PUT answers with the edited row. */
	echoEdits?: boolean;
	deviceTotal?: number;
	integrationsFail?: boolean;
	/** Whether each PUT to a credential fails, in order; the last entry repeats. */
	putPlan?: { fail?: boolean }[];
	/** The first PUT to a credential is committed but not answered until the test releases it. */
	holdFirstPut?: boolean;
	/** A create is committed on arrival but not answered until the test releases it. */
	holdCreate?: boolean;
	/** A delete of a credential is committed, and the connection is lost before any answer. */
	dropDeleteAnswer?: boolean;
	/** A delete of a credential is committed, and the proxy in front of the API answers 502. */
	gatewayFailsDelete?: boolean;
	/** The first read of each store is not answered until the test releases it. */
	holdCredentialReads?: boolean;
	holdSecretReads?: boolean;
}

/**
 * A response the test controls: `arrived` resolves when the request reaches the
 * mocked server, and nothing is answered until `release()` is called. Used instead
 * of fixed delays, so an overlap is whatever the test says it is and not whatever
 * the machine's speed happened to make it.
 */
function gate() {
	let release!: () => void;
	let markArrived!: () => void;
	const released = new Promise<void>((resolve) => (release = resolve));
	const arrived = new Promise<void>((resolve) => (markArrived = resolve));
	return { release, markArrived, released, arrived };
}

/** The calls the page made, plus the mocked server's rows so a test can change them. */
type Calls = Call[] & {
	server: { creds: Cred[]; secrets: typeof SECRETS; failCredentialReads: boolean };
	put: ReturnType<typeof gate>;
	create: ReturnType<typeof gate>;
	credentialRead: ReturnType<typeof gate>;
	secretRead: ReturnType<typeof gate>;
};

interface Call {
	method: string;
	path: string;
	body: Record<string, unknown> | null;
	offset?: number;
}

const json = (route: Route, body: unknown, status = 200) =>
	route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

async function mockVault(page: Page, opts: Options = {}): Promise<Calls> {
	const state = { creds: [...(opts.creds ?? CREDS)], secrets: [...(opts.secrets ?? SECRETS)], failCredentialReads: false };
	const calls = Object.assign([] as Call[], {
		server: state,
		put: gate(),
		create: gate(),
		credentialRead: gate(),
		secretRead: gate()
	}) as Calls;
	const session = {
		accessToken: 'dev.fake.token',
		refreshToken: 'dev.fake.refresh',
		userId: '00000000-0000-0000-0000-000000000001',
		username: 'admin',
		role: opts.role ?? 'admin',
		expiresAt: 4102444800000
	};
	await page.addInitScript(
		`try { localStorage.setItem('nashira:auth', ${JSON.stringify(JSON.stringify(session))}); } catch {}`
	);
	// Anything not answered below fails the way an unreachable backend would.
	await page.route('**/api/**', (route) => route.abort());
	await mockManifest(page, opts.modules);
	await page.route('**/api/navigation-permissions/me', (route) => json(route, opts.visibility ?? []));
	await page.route('**/api/navigation-permissions/roles/*', (route) => {
		const request = route.request();
		if (request.method() === 'GET') return json(route, opts.roleRows ?? []);
		calls.push({ method: 'PUT', path: 'navigation-permissions/roles', body: request.postDataJSON() });
		return json(route, []);
	});
	await page.route('**/api/users?**', (route) => json(route, { total: 0, limit: 200, offset: 0, items: [] }));

	let credentialReads = 0;
	let secretReads = 0;
	let puts = 0;
	await page.route('**/api/credential?**', async (route) => {
		credentialReads++;
		const url = new URL(route.request().url());
		const limit = Math.min(Number(url.searchParams.get('limit')) || 100, 200);
		const offset = Number(url.searchParams.get('offset')) || 0;
		// Recorded on arrival, before any failure or delay: "was it asked for" is
		// the question the blocked-page tests put.
		calls.push({ method: 'GET', path: 'credential', body: null, offset });
		if (
			opts.credFail ||
			state.failCredentialReads ||
			(opts.credFailAfter !== undefined && credentialReads > opts.credFailAfter)
		)
			return json(route, { error: 'boom' }, 500);
		if (opts.holdCredentialReads && credentialReads === 1) {
			calls.credentialRead.markArrived();
			await calls.credentialRead.released;
		}
		if (opts.unstable) {
			const items = offset === 0 ? state.creds.slice(0, 200) : state.creds.slice(199, 399);
			return json(route, { total: state.creds.length + (offset === 0 ? 0 : 1), limit, offset, items });
		}
		return json(route, { total: state.creds.length, limit, offset, items: state.creds.slice(offset, offset + limit) });
	});
	await page.route('**/api/credential/*', async (route) => {
		const request = route.request();
		const id = request.url().split('/').pop()!;
		calls.push({ method: request.method(), path: `credential/${id}`, body: request.postDataJSON() });
		if (request.method() === 'DELETE') {
			state.creds = state.creds.filter((c) => c.credential_id !== id);
			if (opts.dropDeleteAnswer) return route.abort('connectionreset');
			if (opts.gatewayFailsDelete) return json(route, { error: 'backend_unreachable: socket hang up' }, 502);
			return json(route, {});
		}
		const nth = puts++;
		const plan = opts.putPlan?.[Math.min(nth, opts.putPlan.length - 1)];
		const body = request.postDataJSON();
		if (opts.holdFirstPut && nth === 0) {
			calls.put.markArrived();
			await calls.put.released;
		}
		if (plan?.fail ?? opts.saveFail) return json(route, { error: 'credential_name_taken', message: 'That name is taken.' }, 409);
		const current = state.creds.find((c) => c.credential_id === id) ?? CREDS.find((c) => c.credential_id === id) ?? state.creds[0];
		if (!opts.echoEdits) return json(route, current);
		const edited = { ...current, name: body.name };
		state.creds = state.creds.map((c) => (c.credential_id === id ? edited : c));
		return json(route, edited);
	});
	await page.route('**/api/credential', async (route) => {
		const body = route.request().postDataJSON();
		calls.push({ method: 'POST', path: 'credential', body });
		const created = cred('new1', body.name, body.type ?? 'ssh', body.username ?? null, body.auth_method ?? 'password');
		if (opts.holdCreate) {
			state.creds = [...state.creds, created];
			calls.create.markArrived();
			await calls.create.released;
		}
		return json(route, created);
	});
	await page.route('**/api/secrets', async (route) => {
		const request = route.request();
		if (request.method() === 'GET') {
			calls.push({ method: 'GET', path: 'secrets', body: null });
			if (opts.holdSecretReads && !secretReads++) {
				calls.secretRead.markArrived();
				await calls.secretRead.released;
			}
			return opts.secretFail ? json(route, { error: 'boom' }, 500) : json(route, state.secrets);
		}
		calls.push({ method: 'POST', path: 'secrets', body: request.postDataJSON() });
		return json(route, state.secrets[0]);
	});
	await page.route('**/api/secrets/*', (route) => {
		const request = route.request();
		const id = request.url().split('/').pop()!;
		calls.push({ method: request.method(), path: `secrets/${id}`, body: request.postDataJSON() });
		if (request.method() !== 'DELETE') return json(route, state.secrets[0]);
		state.secrets = state.secrets.filter((x) => x.secret_id !== id);
		return route.fulfill({ status: 204, body: '' });
	});

	const list = (items: unknown[], total?: number) => ({ total: total ?? items.length, limit: 200, offset: 0, items });
	await page.route('**/api/device?**', (route) =>
		json(route, list([{ device_id: 'd1', credential_id: 'c1' }, { device_id: 'd2', credential_id: 'c1' }], opts.deviceTotal))
	);
	await page.route('**/api/git/repositories?**', (route) => json(route, list([{ repository_id: 'r1', auth_credential_id: 'c3' }])));
	await page.route('**/api/integrations?**', (route) =>
		opts.integrationsFail ? json(route, { error: 'x' }, 500) : json(route, list([{ integration_id: 'i1', auth_credential_id: 'c4' }]))
	);
	await page.route('**/api/mcp/servers?**', (route) => json(route, list([])));
	return calls;
}

const row = (page: Page, name: string) => page.getByRole('row').filter({ hasText: name });
const dialog = (page: Page) => page.getByRole('dialog');
const nameField = (page: Page) => dialog(page).getByRole('textbox', { name: /^Name/ });
const chip = (page: Page, label: RegExp) => page.getByRole('group', { name: 'Filter by type' }).getByRole('button', { name: label });
const sent = (calls: Call[], method: string, path: string) => calls.find((c) => c.method === method && c.path === path);
/** Reads of either store — what a page that is not allowed to mount must never make. */
const storeReads = (calls: Call[]) => calls.filter((c) => c.method === 'GET' && (c.path === 'credential' || c.path === 'secrets'));

/**
 * Makes one request answer in two parts: its headers at once, its body only when
 * the test finishes it. That is the stall the write deadline exists for — the
 * shared client's own timeout stops at the headers, so a body that never ends is
 * otherwise waited on for ever. The request does not reach the mocked server;
 * the test changes the server's rows itself to say whether it was committed.
 */
async function stallBody(page: Page, method: string, pathEnd: string) {
	await page.addInitScript(
		({ method, pathEnd }) => {
			const real = window.fetch.bind(window);
			const hooks = window as unknown as { __stalled?: boolean; __finishBody?: (body: unknown) => void };
			window.fetch = (input, init) => {
				const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
				const asked = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase();
				if (asked !== method || !new URL(url, location.href).pathname.endsWith(pathEnd)) return real(input, init);
				const body = new ReadableStream<Uint8Array>({
					start(controller) {
						hooks.__finishBody = (answer) => {
							controller.enqueue(new TextEncoder().encode(JSON.stringify(answer)));
							controller.close();
						};
					}
				});
				hooks.__stalled = true;
				return Promise.resolve(new Response(body, { status: 200, headers: { 'Content-Type': 'application/json' } }));
			};
		},
		{ method, pathEnd }
	);
}
/**
 * The first matching request is sent as usual, so the server does its work; its
 * answer then reaches the page with the status and headers intact and a body that
 * fails partway, as when the connection drops in the middle of the response. Later
 * matching requests are left alone, so a retry would be seen as a second call.
 */
async function breakBody(page: Page, method: string, pathEnd: string) {
	await page.addInitScript(
		({ method, pathEnd }) => {
			const real = window.fetch.bind(window);
			let broken = false;
			window.fetch = async (input, init) => {
				const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
				const asked = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase();
				if (broken || asked !== method || !new URL(url, location.href).pathname.endsWith(pathEnd)) return real(input, init);
				broken = true;
				const answer = await real(input, init);
				const body = new ReadableStream<Uint8Array>({
					start(controller) {
						controller.enqueue(new TextEncoder().encode('{"credential_id":"c1","na'));
						controller.error(new TypeError('network error'));
					}
				});
				return new Response(body, { status: answer.status, headers: answer.headers });
			};
		},
		{ method, pathEnd }
	);
}
const stalled = (page: Page) => page.evaluate(() => (window as unknown as { __stalled?: boolean }).__stalled === true);
const finishBody = (page: Page, answer: unknown) =>
	page.evaluate((a) => (window as unknown as { __finishBody: (body: unknown) => void }).__finishBody(a), answer);
/** Past the 45 s write deadline, without waiting for it. */
const PAST_THE_DEADLINE = 46_000;
/** The API client gives up on a request whose headers have not arrived after 30 s. */
const PAST_THE_CLIENT_TIMEOUT = 31_000;

test.use({ viewport: { width: 1440, height: 900 } });

test.describe('the list', () => {
	test('shows both stores, with each entry as one of six kinds', async ({ page }) => {
		await mockVault(page);
		await page.goto('/admin/vault');

		// Older rows keep their kind however the method was spelled.
		await expect(row(page, 'legacy-key').getByText('SSH key')).toBeVisible();
		await expect(row(page, 'legacy-token').getByText('Access token')).toBeVisible();
		await expect(row(page, 'legacy-oauth').getByText('OAuth2 client')).toBeVisible();
		await expect(row(page, 'netbox-token').getByText('Secret value')).toBeVisible();

		// What the two old pages showed is still here.
		await expect(row(page, 'empty-one').getByText('empty', { exact: true })).toBeVisible();
		await expect(row(page, 'netbox-token').getByText('set', { exact: true })).toBeVisible();
		await expect(row(page, 'servicenow-api').getByText('svc · header X-Api-Key')).toBeVisible();
		await expect(chip(page, /^All/)).toContainText('7');
	});

	test('an API key with no header is described by the header that is actually sent', async ({ page }) => {
		await mockVault(page, { creds: [cred('k1', 'bare-key', 'x', null, 'api_key')] });
		await page.goto('/admin/vault');

		await expect(row(page, 'bare-key').getByText('header X-API-Key (default)')).toBeVisible();
		await row(page, 'bare-key').getByRole('button', { name: 'Edit entry' }).click();
		await expect(dialog(page).getByText(/Blank → X-API-Key/)).toBeVisible();
	});

	test('known links report what was found and never claim an entry is unused', async ({ page }) => {
		await mockVault(page, { deviceTotal: 900, integrationsFail: true });
		await page.goto('/admin/vault');

		await expect(row(page, 'core-ssh').getByText('2 devices')).toBeVisible();
		const note = page.getByText('Known links were read from');
		await expect(note).toContainText('Only the first 200 devices were checked');
		await expect(note).toContainText('Not available here: integrations');
		await expect(note).toContainText('an empty cell does not mean unused');
		await expect(page.getByText('Not linked')).toHaveCount(0);
	});

	test('Details sorts by username, with rows that have none at the end', async ({ page }) => {
		await mockVault(page, {
			creds: [cred('a', 'row-zoe', 'x', 'zoe', 'password'), cred('b', 'row-nouser', 'x', null, 'api_key'), cred('c', 'row-adam', 'x', 'adam', 'password')],
			secrets: []
		});
		await page.goto('/admin/vault');
		await expect(page.getByText('row-zoe')).toBeVisible();

		await page.getByRole('columnheader', { name: 'Details' }).getByRole('button').click();
		await expect(page.locator('tbody tr td:first-child')).toHaveText(['row-adam', 'row-zoe', 'row-nouser']);
	});

	test('reads every page of credentials, so counts and search cover the whole store', async ({ page }) => {
		const many = Array.from({ length: 450 }, (_, i) =>
			cred(`m${i}`, `cred-${String(i).padStart(3, '0')}`, 'ssh', 'u', i === 449 ? 'oauth2' : 'password')
		);
		const calls = await mockVault(page, { creds: many });
		await page.goto('/admin/vault');
		await expect(page.getByText('cred-000')).toBeVisible();

		expect(calls.filter((c) => c.method === 'GET' && c.path === 'credential').map((c) => c.offset)).toEqual([0, 200, 400]);
		await expect(chip(page, /^OAuth2 client/)).toContainText('1');
		await page.getByPlaceholder('Search the Vault…').fill('cred-449');
		await expect(page.getByText('cred-449')).toBeVisible();
	});

	test('a list that changes while it is read is never shown twice or passed off as complete', async ({ page }) => {
		const many = Array.from({ length: 399 }, (_, i) => cred(`u${i}`, `unst-${String(i).padStart(3, '0')}`, 'ssh', 'u', 'password'));
		const errors: string[] = [];
		page.on('pageerror', (e) => errors.push(String(e)));
		await mockVault(page, { creds: many, unstable: true });
		await page.goto('/admin/vault');

		await expect(page.getByText('may be incomplete')).toBeVisible({ timeout: 30000 });
		await expect(chip(page, /^Login/)).toContainText('+');
		await page.getByPlaceholder('Search the Vault…').fill('unst-199');
		await expect(row(page, 'unst-199')).toHaveCount(1);
		expect(errors).toEqual([]);
	});
});

test.describe('when a store fails or lags', () => {
	test('secrets down: credentials still work, and the secret count is unknown, not zero', async ({ page }) => {
		await mockVault(page, { secretFail: true });
		await page.goto('/admin/vault');

		await expect(page.getByText('core-ssh')).toBeVisible();
		await expect(page.getByText('Secret values could not be loaded')).toBeVisible();
		await expect(chip(page, /^Secret value/)).toContainText('?');
	});

	test('credentials down: secret values still work', async ({ page }) => {
		await mockVault(page, { credFail: true });
		await page.goto('/admin/vault');

		await expect(page.getByText('netbox-token').first()).toBeVisible();
		await expect(page.getByText('Logins, keys and tokens could not be loaded')).toBeVisible();
	});

	test('a failed store is not reported as an empty Vault', async ({ page }) => {
		await mockVault(page, { credFail: true, secrets: [] });
		await page.goto('/admin/vault?type=secret');

		await expect(page.getByText('No secret value entries yet.')).toBeVisible();
		await expect(page.getByText('The Vault is empty')).toHaveCount(0);
	});

	test('a slow credential read does not hold back secrets that already arrived', async ({ page }) => {
		const calls = await mockVault(page, { holdCredentialReads: true });
		await page.goto('/admin/vault');
		await calls.credentialRead.arrived;

		// Credentials are still out: secrets are listed, credentials are not.
		await expect(page.getByText('netbox-token').first()).toBeVisible();
		await expect(page.getByText('core-ssh')).toHaveCount(0);

		calls.credentialRead.release();
		await expect(page.getByText('core-ssh')).toBeVisible();
	});

	test('a deleted entry stays gone when the refresh after it fails', async ({ page }) => {
		await mockVault(page, { credFailAfter: 1 });
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Delete entry' }).click();
		await page.getByRole('button', { name: 'Delete', exact: true }).click();
		await expect(page.getByText('Logins, keys and tokens could not be loaded')).toBeVisible();
		await expect(page.getByText('core-ssh')).toHaveCount(0);
		await expect(page.getByText('legacy-key')).toBeVisible();
	});

	test('a search does not report "no match" for a store that is still loading', async ({ page }) => {
		const calls = await mockVault(page, { holdSecretReads: true });
		await page.goto('/admin/vault');
		await calls.secretRead.arrived;
		await expect(page.getByText('core-ssh')).toBeVisible();

		// Secrets are still out. Looking for one of them is not a miss: the count
		// says the store is unknown, the credential rows are filtered away, and
		// nothing claims there is no match.
		await page.getByPlaceholder('Search the Vault…').fill('netbox-token');
		await expect(page.getByText('core-ssh')).toHaveCount(0);
		await expect(chip(page, /^Secret value/)).toContainText('?');
		await expect(page.getByText('Nothing matches this search.')).toHaveCount(0);

		calls.secretRead.release();
		await expect(page.getByText('netbox-token').first()).toBeVisible();
	});

	test('a secret deleted here and restored elsewhere comes back on refresh', async ({ page }) => {
		const calls = await mockVault(page);
		await page.goto('/admin/vault');

		await row(page, 'netbox-token').getByRole('button', { name: 'Delete entry' }).click();
		await page.getByRole('button', { name: 'Delete', exact: true }).click();
		await expect(page.getByText('netbox-token')).toHaveCount(0);

		// The agent's set_secret tool revives a deleted name under its old id.
		calls.server.secrets = [...calls.server.secrets, SECRETS[0]];
		await page.getByRole('button', { name: 'Refresh' }).click();
		await expect(page.getByText('netbox-token').first()).toBeVisible();
	});

	test('a second edit that fails does not erase a first that succeeded', async ({ page }) => {
		const calls = await mockVault(page, { echoEdits: true, holdFirstPut: true, putPlan: [{}, { fail: true }] });
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
		await nameField(page).fill('first-edit');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await calls.put.arrived; // the first edit is on the wire and unanswered
		await dialog(page).getByRole('button', { name: 'Cancel' }).click();

		// Retrying what looks stalled: the second edit waits its turn.
		await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
		await nameField(page).fill('second-edit');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await page.waitForTimeout(300);
		expect(calls.filter((c) => c.method === 'PUT')).toHaveLength(1);

		calls.put.release(); // the first succeeds; then the second goes out and fails
		await expect(dialog(page).getByText(/That name is taken|credential_name_taken/)).toBeVisible();
		await dialog(page).getByRole('button', { name: 'Cancel' }).click();
		await expect(row(page, 'first-edit')).toBeVisible();
		await expect(page.getByText('second-edit')).toHaveCount(0);
	});

	test('a create that answers late does not bring back an entry deleted meanwhile', async ({ page }) => {
		const calls = await mockVault(page, { holdCreate: true });
		await page.goto('/admin/vault');
		await expect(page.getByText('core-ssh')).toBeVisible();

		await page.getByRole('button', { name: 'New entry' }).click();
		await dialog(page).getByRole('button', { name: /^Login/ }).click();
		await nameField(page).fill('slow-new');
		await dialog(page).getByRole('button', { name: 'Add to Vault' }).click();
		await calls.create.arrived; // committed on the server, not yet answered
		await dialog(page).getByRole('button', { name: 'Cancel' }).click();

		// A refresh finds it, and the admin deletes it. The delete waits for the create.
		await page.getByRole('button', { name: 'Refresh' }).click();
		await row(page, 'slow-new').getByRole('button', { name: 'Delete entry' }).click();
		await page.getByRole('button', { name: 'Delete', exact: true }).click();
		await page.waitForTimeout(300);
		expect(calls.filter((c) => c.method === 'DELETE')).toHaveLength(0);

		// From here no read succeeds, so only what the page did locally decides what shows.
		calls.server.failCredentialReads = true;
		calls.create.release();
		await expect.poll(() => calls.filter((c) => c.method === 'DELETE').length).toBe(1);
		await expect(page.getByText('Logins, keys and tokens could not be loaded')).toBeVisible();
		await expect(page.getByText('slow-new')).toHaveCount(0);
	});

	test('a write withdrawn while it waited is never sent', async ({ page }) => {
		const calls = await mockVault(page, { echoEdits: true, holdFirstPut: true });
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
		await nameField(page).fill('first-edit');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await calls.put.arrived;
		await dialog(page).getByRole('button', { name: 'Cancel' }).click();

		// A second save is queued behind the first, then its form is closed.
		await row(page, 'legacy-key').getByRole('button', { name: 'Edit entry' }).click();
		await nameField(page).fill('never-sent');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await dialog(page).getByRole('button', { name: 'Cancel' }).click();

		calls.put.release();
		await expect(row(page, 'first-edit')).toBeVisible();
		await page.waitForTimeout(500);
		expect(calls.filter((c) => c.method === 'PUT').map((c) => c.path)).toEqual(['credential/c1']);
		await expect(page.getByText('never-sent')).toHaveCount(0);
	});

	test('an edit that answers after its entry was deleted does not bring the row back', async ({ page }) => {
		const calls = await mockVault(page, { echoEdits: true, holdFirstPut: true });
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
		await nameField(page).fill('core-ssh-edited');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await calls.put.arrived;
		await dialog(page).getByRole('button', { name: 'Cancel' }).click();
		await row(page, 'core-ssh').getByRole('button', { name: 'Delete entry' }).click();
		await page.getByRole('button', { name: 'Delete', exact: true }).click();

		// The delete is held back until the edit before it has answered.
		await page.waitForTimeout(300);
		expect(calls.filter((c) => c.method === 'DELETE')).toHaveLength(0);

		// No read succeeds from here on, so a row put back locally would stay visible.
		calls.server.failCredentialReads = true;
		calls.put.release();
		await expect.poll(() => calls.filter((c) => c.method === 'DELETE').length).toBe(1);
		await expect(page.getByText('Logins, keys and tokens could not be loaded')).toBeVisible();
		await expect(page.getByText('core-ssh')).toHaveCount(0);
		await expect(page.getByText('core-ssh-edited')).toHaveCount(0);
	});

	test('a save whose answer never finishes says so, and its late answer re-reads the list', async ({ page }) => {
		const calls = await mockVault(page);
		await stallBody(page, 'PUT', '/credential/c1');
		await page.clock.install();
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
		await nameField(page).fill('late-edit');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await expect.poll(() => stalled(page)).toBe(true);

		// The server committed the change; only its answer is stuck. The re-read
		// made at the deadline fails, so the list is left showing the old name.
		const edited = { ...calls.server.creds.find((c) => c.credential_id === 'c1')!, name: 'late-edit' };
		calls.server.creds = calls.server.creds.map((c) => (c.credential_id === 'c1' ? edited : c));
		calls.server.failCredentialReads = true;
		await page.clock.fastForward(PAST_THE_DEADLINE);

		// Not "it failed": the admin is told the outcome is unknown.
		await expect(dialog(page).getByText('The change may or may not have been saved')).toBeVisible();
		await expect(page.getByText('Logins, keys and tokens could not be loaded')).toBeVisible();
		await dialog(page).getByRole('button', { name: 'Cancel' }).click();
		await expect(row(page, 'late-edit')).toHaveCount(0);

		// The answer finally arrives. It is not applied and nothing is announced,
		// but the list is read again and now shows what the server holds.
		calls.server.failCredentialReads = false;
		await finishBody(page, { ...edited, name: 'answer-not-applied' });
		await expect(row(page, 'late-edit')).toBeVisible();
		await expect(page.getByText('answer-not-applied')).toHaveCount(0);
		await expect(row(page, 'core-ssh')).toHaveCount(0);
		await expect(page.getByText('Login updated')).toHaveCount(0);
		expect(calls.filter((c) => c.method === 'PUT')).toHaveLength(0);
	});

	test('a delete whose answer never finishes says so instead of claiming it failed', async ({ page }) => {
		const calls = await mockVault(page);
		await stallBody(page, 'DELETE', '/credential/c1');
		await page.clock.install();
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Delete entry' }).click();
		await page.getByRole('button', { name: 'Delete', exact: true }).click();
		await expect.poll(() => stalled(page)).toBe(true);

		// Committed on the server, never answered.
		calls.server.creds = calls.server.creds.filter((c) => c.credential_id !== 'c1');
		await page.clock.fastForward(PAST_THE_DEADLINE);

		await expect(page.getByText('The change may or may not have been saved')).toBeVisible();
		await expect(page.getByText("Couldn't delete the entry")).toHaveCount(0);
		// The re-read made at the deadline shows the row is in fact gone.
		await expect(row(page, 'core-ssh')).toHaveCount(0);
		await expect(page.getByText('Login deleted')).toHaveCount(0);
	});

	test('a save the client gives up on is reported as unknown, not failed, and the list is re-read', async ({ page }) => {
		const calls = await mockVault(page, { holdFirstPut: true });
		await page.clock.install();
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
		await nameField(page).fill('timed-out-edit');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await calls.put.arrived;

		// The server made the change; no answer ever starts, so the client's own
		// timeout ends the request before the write deadline is reached.
		const edited = { ...calls.server.creds.find((c) => c.credential_id === 'c1')!, name: 'timed-out-edit' };
		calls.server.creds = calls.server.creds.map((c) => (c.credential_id === 'c1' ? edited : c));
		const readsBefore = storeReads(calls).length;
		await page.clock.fastForward(PAST_THE_CLIENT_TIMEOUT);

		await expect(dialog(page).getByText('The change may or may not have been saved')).toBeVisible();
		await expect(page.getByText('Please try again')).toHaveCount(0);
		// Re-read without being asked, so the list already shows what the server holds.
		await expect.poll(() => storeReads(calls).length).toBeGreaterThan(readsBefore);
		await dialog(page).getByRole('button', { name: 'Cancel' }).click();
		await expect(row(page, 'timed-out-edit')).toBeVisible();
		await expect(row(page, 'core-ssh')).toHaveCount(0);
		await expect(page.getByText('Login updated')).toHaveCount(0);
		expect(calls.filter((c) => c.method === 'PUT')).toHaveLength(1);
	});

	test('a delete whose connection is lost is reported as unknown, and the list is re-read', async ({ page }) => {
		const calls = await mockVault(page, { dropDeleteAnswer: true });
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Delete entry' }).click();
		const readsBefore = storeReads(calls).length;
		await page.getByRole('button', { name: 'Delete', exact: true }).click();

		await expect(page.getByText('The change may or may not have been saved')).toBeVisible();
		await expect(page.getByText("Couldn't delete the entry")).toHaveCount(0);
		await expect(page.getByText('Login deleted')).toHaveCount(0);
		// The delete had been made: the re-read shows the row is gone.
		await expect.poll(() => storeReads(calls).length).toBeGreaterThan(readsBefore);
		await expect(row(page, 'core-ssh')).toHaveCount(0);
		expect(calls.filter((c) => c.method === 'DELETE')).toHaveLength(1);
	});

	test('a delete answered 502 by the proxy is reported as unknown, and the list is re-read', async ({ page }) => {
		const calls = await mockVault(page, { gatewayFailsDelete: true });
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Delete entry' }).click();
		const readsBefore = storeReads(calls).length;
		await page.getByRole('button', { name: 'Delete', exact: true }).click();

		await expect(page.getByText('The change may or may not have been saved')).toBeVisible();
		await expect(page.getByText("Couldn't delete the entry")).toHaveCount(0);
		await expect(page.getByText('Login deleted')).toHaveCount(0);
		// The backend had made the delete before the proxy lost it.
		await expect.poll(() => storeReads(calls).length).toBeGreaterThan(readsBefore);
		await expect(row(page, 'core-ssh')).toHaveCount(0);
		expect(calls.filter((c) => c.method === 'DELETE')).toHaveLength(1);
	});

	test('a save whose answer breaks partway is reported as unknown, and the list is re-read', async ({ page }) => {
		// The mocked server applies the edit when the PUT reaches it; only the
		// answer is cut off on its way back.
		const calls = await mockVault(page, { echoEdits: true });
		await breakBody(page, 'PUT', '/credential/c1');
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
		await nameField(page).fill('half-answered-edit');
		const readsBefore = storeReads(calls).length;
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();

		await expect(dialog(page).getByText('The change may or may not have been saved')).toBeVisible();
		await expect(page.getByText('Please try again')).toHaveCount(0);
		await expect.poll(() => storeReads(calls).length).toBeGreaterThan(readsBefore);
		await dialog(page).getByRole('button', { name: 'Cancel' }).click();
		await expect(row(page, 'half-answered-edit')).toBeVisible();
		await expect(row(page, 'core-ssh')).toHaveCount(0);
		await expect(page.getByText('Login updated')).toHaveCount(0);
		// Sent once, with what was typed, and not sent again on its own.
		const puts = calls.filter((c) => c.method === 'PUT');
		expect(puts).toHaveLength(1);
		expect(puts[0].body).toMatchObject({ name: 'half-answered-edit' });
	});

	test('a write still waiting its turn is not sent once the Vault has been left', async ({ page }) => {
		const calls = await mockVault(page, { echoEdits: true, holdFirstPut: true });
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
		await nameField(page).fill('first-edit');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await calls.put.arrived;
		await dialog(page).getByRole('button', { name: 'Cancel' }).click();

		// A delete is confirmed and queued behind the edit; then the admin leaves.
		await row(page, 'legacy-key').getByRole('button', { name: 'Delete entry' }).click();
		await page.getByRole('button', { name: 'Delete', exact: true }).click();
		await page.getByRole('link', { name: 'Navigation access', exact: true }).click();
		await expect(page).toHaveURL(/navigation-permissions/);
		const readsBefore = storeReads(calls).length;

		// The edit was already on the wire and is seen through. The delete never
		// went out, the admin is told so, and nothing is read for a page that is gone.
		calls.put.release();
		await expect(page.getByText('The delete of "legacy-key" was not sent')).toBeVisible();
		expect(calls.filter((c) => c.method === 'DELETE')).toHaveLength(0);
		expect(storeReads(calls)).toHaveLength(readsBefore);
	});

	test('a save left unanswered after the Vault was left says so in a toast and reads nothing', async ({ page }) => {
		const calls = await mockVault(page);
		await stallBody(page, 'PUT', '/credential/c1');
		await page.clock.install();
		await page.goto('/admin/navigation-permissions');
		await page.getByRole('link', { name: 'Vault', exact: true }).click();

		await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
		await nameField(page).fill('left-behind');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await expect.poll(() => stalled(page)).toBe(true);

		// Back to where the admin came from, with the form still open.
		await page.goBack();
		await expect(page).toHaveURL(/navigation-permissions/);
		await expect(page.getByRole('heading', { name: 'Vault', level: 1 })).toHaveCount(0);
		const readsBefore = storeReads(calls).length;

		// The form is gone, so the message has to reach the admin some other way.
		await page.clock.fastForward(PAST_THE_DEADLINE);
		await expect(page.getByText('The change may or may not have been saved')).toBeVisible();

		// The answer arrives after all: nothing is read on behalf of a page that is gone.
		await finishBody(page, { ...calls.server.creds.find((c) => c.credential_id === 'c1')!, name: 'left-behind' });
		await page.waitForTimeout(300);
		expect(storeReads(calls)).toHaveLength(readsBefore);
	});
});

test.describe('layout', () => {
	// The table used to be wider than the page at every desktop width, so the
	// Delete buttons sat behind a horizontal scroll and looked missing. Playwright
	// scrolls before it clicks, so no other test notices.
	for (const width of [1280, 1440, 1920])
		test(`every row's actions are on screen at ${width}px, long names and all`, async ({ page }) => {
			await page.setViewportSize({ width, height: 900 });
			await mockVault(page, {
				secrets: [
					...SECRETS,
					{ secret_id: 's3', name: 'servicenow-production-oauth-client-secret', description: 'Client secret for the ServiceNow production instance', has_value: true, created_by: 'admin', created_at: TS, updated_at: TS }
				]
			});
			await page.goto('/admin/vault');
			await expect(row(page, 'core-ssh')).toContainText('2 devices');

			const fit = await page.evaluate(() => {
				const wrap = document.querySelector('table')!.parentElement!;
				const right = wrap.getBoundingClientRect().right;
				const deletes = [...document.querySelectorAll('[aria-label="Delete entry"]')];
				return { overflow: wrap.scrollWidth - wrap.clientWidth, cut: deletes.filter((b) => b.getBoundingClientRect().right > right + 1).length };
			});
			expect(fit).toEqual({ overflow: 0, cut: 0 });
		});

	test('form titles keep the capitals of SSH, API and OAuth2', async ({ page }) => {
		await mockVault(page);
		await page.goto('/admin/vault');
		await page.getByRole('button', { name: 'New entry' }).click();
		await dialog(page).getByRole('button', { name: /^API key/ }).click();
		await expect(dialog(page)).toContainText('New API key');
		await dialog(page).getByRole('button', { name: 'Type' }).click();
		await dialog(page).getByRole('button', { name: /^Login/ }).click();
		await expect(dialog(page)).toContainText('New login');
		await dialog(page).getByRole('button', { name: 'Cancel' }).click();

		await row(page, 'legacy-oauth').getByRole('button', { name: 'Edit entry' }).click();
		await expect(dialog(page)).toContainText('Edit OAuth2 client');
	});
});

test.describe('the entry form', () => {
	test('renaming an older key keeps it a key and sends no secret material', async ({ page }) => {
		const calls = await mockVault(page);
		await page.goto('/admin/vault');

		await row(page, 'legacy-key').getByRole('button', { name: 'Edit entry' }).click();
		await nameField(page).fill('legacy-key-renamed');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();

		await expect.poll(() => sent(calls, 'PUT', 'credential/c2')?.body).toEqual({
			name: 'legacy-key-renamed',
			type: 'ssh',
			username: 'admin',
			auth_method: 'key',
			api_key_header: '',
			client_id: '',
			token_url: '',
			scopes: ''
		});
	});

	test('changing an entry to another kind drops what was typed for the old one', async ({ page }) => {
		const calls = await mockVault(page);
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
		await dialog(page).getByLabel('Password').fill('typed-password');
		await dialog(page).getByLabel('Type').selectOption('api_key');
		await dialog(page).getByLabel('API key value').fill('k-123');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();

		await expect.poll(() => sent(calls, 'PUT', 'credential/c1')?.body).toMatchObject({ auth_method: 'api_key', token: 'k-123' });
		expect(sent(calls, 'PUT', 'credential/c1')!.body).not.toHaveProperty('password');
	});

	test('the type picker keeps a draft for the same type and drops it for another', async ({ page }) => {
		const calls = await mockVault(page);
		await page.goto('/admin/vault');
		await expect(page.getByText('core-ssh')).toBeVisible();

		await page.getByRole('button', { name: 'New entry' }).click();
		await dialog(page).getByRole('button', { name: /^Login/ }).click();
		await expect(nameField(page)).toBeFocused();
		await nameField(page).fill('new-one');
		await dialog(page).getByLabel('Password').fill('keep-me');

		// Back and forward to the same type: nothing typed is lost.
		await dialog(page).getByRole('button', { name: 'Type' }).click();
		await expect(dialog(page).getByRole('button', { name: /^Login/ })).toBeFocused();
		await dialog(page).getByRole('button', { name: /^Login/ }).click();
		await expect(dialog(page).getByLabel('Password')).toHaveValue('keep-me');

		// To a different type: the password must not travel with a token.
		await dialog(page).getByRole('button', { name: 'Type' }).click();
		await dialog(page).getByRole('button', { name: /^Access token/ }).click();
		await dialog(page).getByLabel('Token').fill('tok-1');
		await dialog(page).getByRole('button', { name: 'Add to Vault' }).click();

		await expect.poll(() => sent(calls, 'POST', 'credential')?.body).toMatchObject({
			name: 'new-one',
			// The tag followed the type: the admin never wrote one.
			type: 'token',
			auth_method: 'token',
			token: 'tok-1',
			password: null
		});
	});

	test('a new entry shows the tag it will be stored with, and the tag follows the type until the admin writes one', async ({
		page
	}) => {
		const calls = await mockVault(page);
		await page.goto('/admin/vault');
		await expect(page.getByText('core-ssh')).toBeVisible();

		await page.getByRole('button', { name: 'New entry' }).click();
		await dialog(page).getByRole('button', { name: /^API key/ }).click();
		await expect(dialog(page).getByLabel('Tag')).toHaveValue('api_key');

		// Another type: the untouched tag goes with it.
		await dialog(page).getByRole('button', { name: 'Type' }).click();
		await dialog(page).getByRole('button', { name: /^OAuth2 client/ }).click();
		await expect(dialog(page).getByLabel('Tag')).toHaveValue('oauth2');

		// A tag the admin wrote stays when the type changes.
		await dialog(page).getByLabel('Tag').fill('servicenow');
		await dialog(page).getByRole('button', { name: 'Type' }).click();
		await dialog(page).getByRole('button', { name: /^API key/ }).click();
		await expect(dialog(page).getByLabel('Tag')).toHaveValue('servicenow');

		// Cleared: the kind's default is stored, not the server's blanket "ssh".
		await dialog(page).getByLabel('Tag').fill('');
		await nameField(page).fill('snow-key');
		await dialog(page).getByLabel('API key value').fill('k-1');
		await dialog(page).getByRole('button', { name: 'Add to Vault' }).click();
		await expect.poll(() => sent(calls, 'POST', 'credential')?.body).toMatchObject({ type: 'api_key', auth_method: 'api_key' });
	});

	test('a credential form cannot be edited while its save is in flight', async ({ page }) => {
		const calls = await mockVault(page, { echoEdits: true, holdFirstPut: true });
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
		await nameField(page).fill('core-ssh-renamed');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await calls.put.arrived;

		// Typing now would be lost when the save closes the form.
		await expect(nameField(page)).toBeDisabled();
		await expect(dialog(page).getByLabel('Tag')).toBeDisabled();
		await expect(dialog(page).getByLabel('Username')).toBeDisabled();
		await expect(dialog(page).getByLabel('Password')).toBeDisabled();

		calls.put.release();
		await expect(dialog(page)).toBeHidden();
		await expect(row(page, 'core-ssh-renamed')).toBeVisible();
	});

	test('known links are read again after a credential is saved or deleted', async ({ page }) => {
		const calls = await mockVault(page, { echoEdits: true });
		await page.goto('/admin/vault');
		await expect(row(page, 'core-ssh')).toContainText('2 devices');
		await expect(row(page, 'legacy-key')).not.toContainText('device');

		// A device is moved onto legacy-key elsewhere; saving the entry brings it in.
		await page.route('**/api/device?**', (route) =>
			json(route, { total: 2, limit: 200, offset: 0, items: [{ device_id: 'd1', credential_id: 'c1' }, { device_id: 'd2', credential_id: 'c2' }] })
		);
		await row(page, 'legacy-key').getByRole('button', { name: 'Edit entry' }).click();
		await nameField(page).fill('legacy-key-2');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await expect(row(page, 'legacy-key-2')).toContainText('1 device');
		await expect(row(page, 'core-ssh')).toContainText('1 device');

		// And after a delete.
		await page.route('**/api/device?**', (route) =>
			json(route, { total: 1, limit: 200, offset: 0, items: [{ device_id: 'd2', credential_id: 'c2' }] })
		);
		await row(page, 'core-ssh').getByRole('button', { name: 'Delete entry' }).click();
		await page.getByRole('button', { name: 'Delete', exact: true }).click();
		await expect(row(page, 'core-ssh')).toHaveCount(0);
		await expect(row(page, 'legacy-key-2')).toContainText('1 device');
		expect(sent(calls, 'DELETE', 'credential/c1')).toBeDefined();
	});

	test('a secret name is fixed, a bad one is refused, and a description edit leaves the value alone', async ({ page }) => {
		const calls = await mockVault(page);
		await page.goto('/admin/vault');

		await row(page, 'netbox-token').getByRole('button', { name: 'Edit entry' }).click();
		await expect(nameField(page)).toBeDisabled();
		await dialog(page).getByLabel('Description').fill('new description');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await expect.poll(() => sent(calls, 'PUT', 'secrets/s1')?.body).toEqual({ description: 'new description' });

		await page.getByRole('button', { name: 'New entry' }).click();
		await dialog(page).getByRole('button', { name: /^Secret value/ }).click();
		await nameField(page).fill('Bad Name');
		await dialog(page).getByLabel('Value').fill('v');
		await dialog(page).getByRole('button', { name: 'Add to Vault' }).click();
		await expect(dialog(page).getByText('Name must be 2–64 chars')).toBeVisible();
		expect(sent(calls, 'POST', 'secrets')).toBeUndefined();
	});

	test('rotating a secret asks first, and cancelling sends nothing', async ({ page }) => {
		const calls = await mockVault(page);
		await page.goto('/admin/vault');

		await row(page, 'netbox-token').getByRole('button', { name: 'Edit entry' }).click();
		await dialog(page).getByLabel(/New value/).fill('rotated');
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await expect(page.getByText('Rotate "netbox-token"?')).toBeVisible();
		await page.getByRole('button', { name: 'Cancel' }).last().click();

		await page.waitForTimeout(300);
		expect(calls.some((c) => c.method === 'PUT' && c.path.startsWith('secrets/'))).toBe(false);
	});

	test('a save that fails is shown in its form, or as a toast if the form is gone', async ({ page }) => {
		const calls = await mockVault(page, { saveFail: true, holdFirstPut: true });
		await page.goto('/admin/vault');

		await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await calls.put.arrived;
		// Cancel stays usable while the request is out.
		await expect(dialog(page).getByRole('button', { name: 'Cancel' })).toBeEnabled();
		await page.keyboard.press('Escape');
		await expect(dialog(page)).toBeHidden();

		// The form is gone when the failure arrives, so it has to come as a toast.
		calls.put.release();
		await expect(page.getByText(/Failed to save the login|That name is taken/).first()).toBeVisible();

		// With the form still open, the same failure is shown in it.
		await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
		await dialog(page).getByRole('button', { name: 'Save changes' }).click();
		await expect(dialog(page).getByText(/That name is taken|credential_name_taken/)).toBeVisible();
	});
});

test.describe('addresses', () => {
	test('?new=1 opens the picker once, and the palette action works again while here', async ({ page }) => {
		await mockVault(page);
		await page.goto('/admin/vault?new=1');

		await expect(dialog(page).getByText('What are you storing?')).toBeVisible();
		await expect(page).not.toHaveURL(/new=1/);
		await dialog(page).getByRole('button', { name: 'Cancel' }).click();
		await expect(dialog(page)).toBeHidden();

		for (let round = 0; round < 2; round++) {
			await page.keyboard.press('Control+k');
			await page.keyboard.type('New Vault entry');
			await page.keyboard.press('Enter');
			await expect(dialog(page).getByText('What are you storing?')).toBeVisible();
			await expect(page).not.toHaveURL(/new=1/);
			await dialog(page).getByRole('button', { name: 'Cancel' }).click();
			await expect(dialog(page)).toBeHidden();
		}
	});

	test('the type filter follows the address in both directions', async ({ page }) => {
		await mockVault(page);
		await page.goto('/admin/vault?type=secret');

		await expect(chip(page, /^Secret value/)).toHaveAttribute('aria-pressed', 'true');
		await expect(page.getByText('core-ssh')).toHaveCount(0);

		await chip(page, /^Login/).click();
		await expect(page).toHaveURL(/\?type=login$/);
		await expect(chip(page, /^Login/)).toHaveAttribute('aria-pressed', 'true');

		await chip(page, /^All/).click();
		await expect(page).toHaveURL(/\/admin\/vault$/);
		await expect(page.getByText('core-ssh')).toBeVisible();
	});

	test('the old Credentials and Secrets addresses lead here', async ({ page }) => {
		await mockVault(page);

		await page.goto('/admin/credentials');
		await expect(page).toHaveURL(/\/admin\/vault$/);
		await expect(page.getByText('core-ssh')).toBeVisible();

		await page.goto('/admin/secrets?new=1');
		await expect(page).toHaveURL(/\/admin\/vault\?type=secret$/);
		await expect(dialog(page).getByText('New secret value')).toBeVisible();
	});
});

test.describe('who may see what', () => {
	test('a store whose old page was hidden stays hidden inside the Vault', async ({ page }) => {
		await mockVault(page, { visibility: [{ page_key: '/admin/secrets', visible: false }] });
		await page.goto('/admin/vault?type=secret');

		await expect(page.getByText('core-ssh')).toBeVisible();
		await expect(page.getByText('netbox-token')).toHaveCount(0);
		await expect(chip(page, /^Secret value/)).toHaveCount(0);
		// A type the user may not see is dropped from the address as well.
		await expect(page).toHaveURL(/\/admin\/vault$/);
		await expect(page.getByRole('link', { name: 'Vault', exact: true })).toBeVisible();
	});

	test('both old pages hidden: no Vault on the menu, and the old addresses are refused where they stand', async ({ page }) => {
		const hidden = [
			{ page_key: '/admin/secrets', visible: false },
			{ page_key: '/admin/credentials', visible: false }
		];
		await mockVault(page, { visibility: hidden });

		await page.goto('/admin/credentials');
		await expect(page.getByRole('main')).toBeVisible();
		await expect(page).toHaveURL(/\/admin\/credentials$/);
		await expect(page.getByRole('heading', { name: 'Vault', level: 1 })).toHaveCount(0);
		await expect(page.getByRole('link', { name: 'Vault', exact: true })).toHaveCount(0);
	});

	test('a setting on the Vault itself wins over the old ones', async ({ page }) => {
		await mockVault(page, {
			visibility: [
				{ page_key: '/admin/secrets', visible: false },
				{ page_key: '/admin/vault', visible: true }
			]
		});
		await page.goto('/admin/vault');

		await expect(page.getByText('netbox-token').first()).toBeVisible();
	});

	test('a non-admin sees neither the Vault nor what it holds', async ({ page }) => {
		const calls = await mockVault(page, { role: 'operator' });
		await page.goto('/admin/vault');

		await expect(page.getByRole('main')).toBeVisible();
		await expect(page.getByRole('heading', { name: 'Vault', level: 1 })).toHaveCount(0);
		await expect(page.getByRole('link', { name: 'Vault', exact: true })).toHaveCount(0);
		// Blocked means not mounted: the page never asked for either store.
		expect(storeReads(calls)).toEqual([]);
	});

	test('a deployment without the secrets module has no Vault', async ({ page }) => {
		const calls = await mockVault(page, { modules: ['fleet', 'integrations'] });

		for (const path of ['/admin/vault', '/admin/credentials', '/admin/secrets']) {
			await page.goto(path);
			await expect(page.getByRole('main')).toBeVisible();
			await expect(page.getByRole('heading', { name: 'Vault', level: 1 })).toHaveCount(0);
		}
		expect(storeReads(calls)).toEqual([]);
	});

	test('the navigation editor shows a carried-over setting and replaces it on save', async ({ page }) => {
		const calls = await mockVault(page, {
			roleRows: [
				{ page_key: '/admin/credentials', visible: false },
				{ page_key: '/admin/secrets', visible: false }
			]
		});
		await page.goto('/admin/navigation-permissions');
		await page.getByLabel('Role').selectOption('admin');

		const vault = page.locator('div.grid').filter({ hasText: '/admin/vault' }).last();
		const setting = vault.locator('select');
		await expect(setting).toHaveValue('carried');
		await expect(setting.locator('option:checked')).toHaveText('Carried over (credentials hidden, secrets hidden)');
		await expect(vault.locator('span').filter({ hasText: /^Hidden$/ })).toBeVisible();

		await setting.selectOption('inherit');
		await page.getByRole('button', { name: /^Save/ }).click();

		// "Use default" has to clear the two old rows too, or it would fall back to them.
		await expect
			.poll(() => (sent(calls, 'PUT', 'navigation-permissions/roles')?.body as { permissions: { page_key: string; inherit: boolean }[] })?.permissions)
			.toEqual([
				{ page_key: '/admin/vault', visible: false, inherit: true },
				{ page_key: '/admin/credentials', visible: false, inherit: true },
				{ page_key: '/admin/secrets', visible: false, inherit: true }
			]);
	});
});
