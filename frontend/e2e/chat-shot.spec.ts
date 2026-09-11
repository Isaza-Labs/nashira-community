import { test } from '@playwright/test';

import { mockManifest } from './deployment';

// Visual capture of a loaded conversation with the kind of markdown the agent
// actually emits (headings, lists, tables, fenced code, inline code). Mocks the
// conversation endpoints so no backend is needed.

const session = {
	accessToken: 'dev.fake.token',
	refreshToken: 'dev.fake.refresh',
	userId: '00000000-0000-0000-0000-000000000001',
	username: 'admin',
	role: 'admin',
	expiresAt: 4102444800000
};

function seed(mode: 'light' | 'dark') {
	return `try {
		localStorage.setItem('nashira:auth', ${JSON.stringify(JSON.stringify(session))});
		localStorage.setItem('nashira:prefs', ${JSON.stringify(JSON.stringify({ mode }))});
	} catch {}`;
}

const CID = '6d893a01-7277-4f00-bb3c-85992d1cd183';

const ASSISTANT = `I checked the three core switches. Here is what I found.

## Summary

The \`core-sw-01\` uplink is saturated. Two devices need attention:

- **core-sw-01** — Gi0/1 at 94% utilisation, sustained for 40 minutes
- **edge-rt-04** — BGP session to AS64512 is flapping
- acc-sw-17 — healthy, no action needed

### Interface detail

| Device | Interface | Util | Errors | Status |
| --- | --- | --- | --- | --- |
| core-sw-01 | Gi0/1 | 94% | 0 | saturated |
| core-sw-01 | Gi0/2 | 12% | 0 | ok |
| edge-rt-04 | Ge-0/0/3 | 38% | 1204 | errors |

### Suggested remediation

Run this on \`core-sw-01\` to confirm the top talkers before changing anything:

\`\`\`
show interfaces gi0/1 counters
show ip cache flow | include Gi0/1
\`\`\`

> Do not shut the interface during business hours — it carries the Madrid voice VLAN.

1. Confirm the top talkers
2. Shift the voice VLAN to Gi0/2
3. Re-measure after 10 minutes

Let me know if you want me to stage the config change.`;

test.use({ viewport: { width: 1440, height: 900 } });

// Chat renders only where the deployment runs it; this capture is of a full install.
test.beforeEach(({ page }) => mockManifest(page));

for (const mode of ['light', 'dark'] as const) {
	test(`chat conversation — ${mode}`, async ({ page }) => {
		await page.addInitScript(seed(mode));

		await page.route('**/api/ai/conversations?**', (route) =>
			route.fulfill({
				contentType: 'application/json',
				body: JSON.stringify({
					total: 1,
					limit: 50,
					offset: 0,
					items: [
						{
							conversation_id: CID,
							title: 'Core switch uplink saturation',
							status: 'active',
							tokens_in: 1840,
							tokens_out: 620,
							created_at: '2026-07-29T09:00:00Z',
							updated_at: '2026-07-29T09:12:00Z'
						}
					]
				})
			})
		);

		await page.route(`**/api/ai/conversations/${CID}`, (route) =>
			route.fulfill({
				contentType: 'application/json',
				body: JSON.stringify({
					conversation_id: CID,
					title: 'Core switch uplink saturation',
					status: 'active',
					tokens_in: 1840,
					tokens_out: 620,
					created_at: '2026-07-29T09:00:00Z',
					updated_at: '2026-07-29T09:12:00Z',
					messages: [
						{ role: 'user', content: 'Check the core switches for uplink saturation.' },
						{ role: 'assistant', content: ASSISTANT }
					]
				})
			})
		);

		await page.goto(`/chat/${CID}`);
		await page.getByText('Suggested remediation').waitFor();
		await page.waitForTimeout(500);
		await page.screenshot({ path: `e2e/shots/chat-conversation-${mode}.png`, fullPage: false });
	});
}
