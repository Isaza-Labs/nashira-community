import { expect, test, type Page, type Route } from '@playwright/test';

import { mockManifest } from './deployment';

// The provider form's guidance placement picker. It is a view over one key of the
// provider's Config JSON (guidance_after_question), so what matters is that the
// picker and the JSON agree, that "Server default" removes the key rather than
// storing a value, and that the picker only appears where the backend uses it.

const TS = '2026-11-01T12:00:00Z';

function provider(id: string, name: string, type: string, config: Record<string, unknown>) {
	return {
		ai_provider_id: id,
		name,
		type,
		base_url: type === 'custom' ? 'http://ring.example:8888/v1' : null,
		default_model: 'm',
		has_api_key: true,
		enabled: true,
		config,
		created_at: TS,
		updated_at: TS
	};
}

const json = (route: Route, body: unknown, status = 200) =>
	route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

async function mockProviders(page: Page, rows: ReturnType<typeof provider>[]) {
	const puts: { id: string; body: Record<string, unknown> }[] = [];
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
	await page.route('**/api/ai/providers?**', (route) =>
		json(route, { total: rows.length, limit: 100, offset: 0, items: rows })
	);
	await page.route('**/api/ai/providers/*', async (route) => {
		const request = route.request();
		if (request.method() !== 'PUT') return route.abort();
		const id = request.url().split('/').pop()!;
		const body = request.postDataJSON() as Record<string, unknown>;
		puts.push({ id, body });
		const old = rows.find((r) => r.ai_provider_id === id)!;
		return json(route, { ...old, config: body.config });
	});
	return puts;
}

function editRow(page: Page, name: string) {
	return page.getByRole('row').filter({ hasText: name }).getByRole('button', { name: 'Edit provider' });
}

const picker = (page: Page) => page.getByLabel('Guidance placement');
const configBox = (page: Page) => page.getByLabel('Config (JSON)');

test('the picker shows what the config holds and writes only its own key', async ({ page }) => {
	const puts = await mockProviders(page, [
		provider('p1', 'ring', 'custom', { models: ['m2'], guidance_after_question: true })
	]);
	await page.goto('/admin/providers');
	await editRow(page, 'ring').click();

	await expect(picker(page)).toHaveValue('after');

	await picker(page).selectOption('before');
	await expect(configBox(page)).toHaveValue(/"guidance_after_question": false/);

	await picker(page).selectOption('default');
	await expect(configBox(page)).not.toHaveValue(/guidance_after_question/);

	await picker(page).selectOption('after');
	await page.getByRole('button', { name: 'Save' }).click();

	await expect.poll(() => puts.length).toBe(1);
	expect(puts[0].body.config).toEqual({ models: ['m2'], guidance_after_question: true });
});

test('choosing Server default saves the config without the key', async ({ page }) => {
	const puts = await mockProviders(page, [provider('p1', 'ring', 'custom', { guidance_after_question: false })]);
	await page.goto('/admin/providers');
	await editRow(page, 'ring').click();

	await picker(page).selectOption('default');
	await page.getByRole('button', { name: 'Save' }).click();

	await expect.poll(() => puts.length).toBe(1);
	expect(puts[0].body.config).toEqual({});
});

test('typing the key into the JSON moves the picker, and a wrong value is refused', async ({ page }) => {
	await mockProviders(page, [provider('p1', 'ring', 'custom', {})]);
	await page.goto('/admin/providers');
	await editRow(page, 'ring').click();
	await expect(picker(page)).toHaveValue('default');

	await configBox(page).fill('{ "guidance_after_question": false }');
	await expect(picker(page)).toHaveValue('before');

	// The backend ignores a value that is not true or false, so the picker shows the
	// server default; the form refuses to save it, and picking a value replaces it.
	await configBox(page).fill('{ "guidance_after_question": "yes" }');
	await expect(page.getByText('"guidance_after_question" must be true or false.')).toBeVisible();
	await expect(picker(page)).toHaveValue('default');
	await picker(page).selectOption('after');
	await expect(configBox(page)).toHaveValue(/"guidance_after_question": true/);
	await expect(page.getByText('"guidance_after_question" must be true or false.')).toHaveCount(0);

	// Only text that is not a JSON object disables the picker.
	await configBox(page).fill('{ "guidance_after_question": true');
	await expect(picker(page)).toBeDisabled();
});

test('a mistake in another key does not change what the picker shows', async ({ page }) => {
	await mockProviders(page, [
		provider('p1', 'ring', 'custom', { guidance_after_question: true, workspace_id: 'Production' })
	]);
	await page.goto('/admin/providers');
	await editRow(page, 'ring').click();

	await expect(page.getByText('"workspace_id" is the id, not the workspace name.', { exact: false })).toBeVisible();
	await expect(picker(page)).toHaveValue('after');
	await expect(picker(page)).toBeEnabled();
});

test('the picker appears only for OpenAI-compatible providers', async ({ page }) => {
	await mockProviders(page, [
		provider('p1', 'claude', 'anthropic', {}),
		provider('p2', 'gemini', 'gemini', {}),
		provider('p3', 'local-ollama', 'ollama', {}),
		provider('p4', 'deepseek-api', 'deepseek', {})
	]);
	await page.goto('/admin/providers');

	for (const name of ['claude', 'gemini', 'local-ollama']) {
		await editRow(page, name).click();
		await expect(configBox(page)).toBeVisible();
		await expect(picker(page)).toHaveCount(0);
		await page.getByRole('button', { name: 'Cancel' }).click();
	}
	await editRow(page, 'deepseek-api').click();
	await expect(picker(page)).toBeVisible();
});
