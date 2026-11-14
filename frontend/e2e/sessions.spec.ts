import { expect, test, type Page, type Route } from '@playwright/test';

import { mockManifest } from './deployment';

// The token line of a turn on /admin/sessions. It shows how much of the prompt was
// read from the provider's cache, and how much was written to it, when the turn
// reports any; a turn that reports none, or one stored before these figures were
// kept, shows the plain "in / out" line it always did.

const TS = '2026-11-12T15:00:00Z';

function turn(id: string, message: string, cache: Record<string, number | null>) {
	return {
		agent_turn_id: id,
		model: 'm',
		user_message: message,
		assistant_text: 'ok',
		tool_calls: [],
		tool_call_count: 0,
		tokens_in: 50000,
		tokens_out: 120,
		...cache,
		iterations: 2,
		status: 'completed',
		error: null,
		started_at: TS,
		elapsed_ms: 900
	};
}

const summary = {
	conversation_id: 's1',
	user_id: '00000000-0000-0000-0000-000000000001',
	username: 'admin',
	title: 'Cache figures',
	status: 'active',
	message_count: 8,
	turn_count: 4,
	tool_call_count: 0,
	failed_turn_count: 0,
	tokens_in: 200000,
	tokens_out: 480,
	created_at: TS,
	updated_at: TS
};

const json = (route: Route, body: unknown) =>
	route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });

async function openSession(page: Page, turns: ReturnType<typeof turn>[]) {
	const session = {
		accessToken: 'dev.fake.token',
		refreshToken: 'dev.fake.refresh',
		userId: '00000000-0000-0000-0000-000000000001',
		username: 'admin',
		role: 'admin',
		expiresAt: 4102444800000
	};
	await page.addInitScript(
		`try { localStorage.setItem('nashira:auth', ${JSON.stringify(JSON.stringify(session))}); } catch {}`
	);
	await page.route('**/api/**', (route) => route.abort());
	await mockManifest(page);
	await page.route('**/api/navigation-permissions/me', (route) => json(route, []));
	await page.route('**/api/sessions?**', (route) =>
		json(route, { total: 1, limit: 50, offset: 0, items: [summary] })
	);
	await page.route('**/api/sessions/s1', (route) => json(route, { ...summary, messages: [], turns }));

	await page.goto('/admin/sessions');
	await page.getByRole('button', { name: 'Open session' }).click();
}

// The token line of one turn, after expanding it.
async function tokenLine(page: Page, message: string) {
	await page.getByRole('button', { name: message }).click();
	return page.getByText(/\d iterations ·/).filter({ hasText: '50,000 in' }).last();
}

test('a turn that read from the cache shows the cached part beside the prompt tokens', async ({ page }) => {
	await openSession(page, [turn('t1', 'read only', { tokens_cache_read: 45000, tokens_cache_write: 0 })]);

	const line = await tokenLine(page, 'read only');
	await expect(line).toContainText(/50,000 in\s*\(45,000 cached\)\s*\/ 120\s+out/);
	await expect(line).not.toContainText('written to cache');
});

test('a turn that also wrote to the cache shows both figures', async ({ page }) => {
	await openSession(page, [turn('t2', 'read and write', { tokens_cache_read: 300, tokens_cache_write: 50 })]);

	const line = await tokenLine(page, 'read and write');
	await expect(line).toContainText(/50,000 in\s*\(300 cached\)\s*· 50 written to cache\s*\/ 120\s+out/);
});

test('a turn with nothing cached shows the plain line', async ({ page }) => {
	await openSession(page, [turn('t3', 'nothing cached', { tokens_cache_read: 0, tokens_cache_write: 0 })]);

	const line = await tokenLine(page, 'nothing cached');
	await expect(line).toContainText(/50,000 in\s*\/ 120\s+out/);
	await expect(line).not.toContainText('cached');
});

test('a turn stored before cache figures were kept shows the plain line', async ({ page }) => {
	// One row with the fields null, as the API returns an older turn, and one without
	// the fields at all, as a backend from before the change would send it.
	await openSession(page, [
		turn('t4', 'null fields', { tokens_cache_read: null, tokens_cache_write: null }),
		turn('t5', 'no fields', {})
	]);

	for (const message of ['null fields', 'no fields']) {
		const line = await tokenLine(page, message);
		await expect(line).toContainText(/50,000 in\s*\/ 120\s+out/);
		await expect(line).not.toContainText('cached');
	}
});
