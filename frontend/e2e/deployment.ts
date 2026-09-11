import type { Page } from '@playwright/test';

// The manifest GET /api/modules answers, for specs that run without a backend.
//
// Every authenticated spec needs it now: the shell asks what this deployment runs
// before it renders a single destination, and a spec that leaves the question
// unanswered is testing the fail-closed path whether it meant to or not.

export const MODULE_IDS = [
	'core',
	'chat',
	'ai-studio',
	'automation',
	'fleet',
	'integrations',
	'communications',
	'secrets',
	'git',
	'knowledge',
	'artifacts',
	'governance',
	'observability'
] as const;

export function manifest(enabled?: readonly string[]) {
	return {
		configuration_mode: enabled ? 'explicit' : 'default_all',
		modules: MODULE_IDS.map((id) => ({
			id,
			enabled: !enabled || id === 'core' || enabled.includes(id),
			configurable: id !== 'core',
			dependencies: []
		}))
	};
}

/** Answers the manifest. With no argument: everything on, as a default deployment. */
export function mockManifest(page: Page, enabled?: readonly string[]) {
	return page.route('**/api/modules', (route) =>
		route.fulfill({ contentType: 'application/json', body: JSON.stringify(manifest(enabled)) })
	);
}
