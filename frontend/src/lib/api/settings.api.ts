// Platform settings (`/api/admin/settings`, Admin).
//
// `effective` is what the platform is using right now; `source` says where that came
// from — a value an admin stored here, an environment variable, or the built-in
// default. Only the first is theirs to change from this screen, and a page that hid the
// difference would leave someone editing a field that configuration keeps overriding.

import { api } from '$lib/api/client';

export type Setting = {
	key: string;
	category: string;
	displayName: string;
	description: string;
	inputType: 'bool' | 'int' | string;
	effective: string;
	/** Null when nobody has overridden this from the screen. */
	storedValue: string | null;
	source: 'stored' | 'configuration' | 'default' | string;
	defaultValue: string;
};

export type Settings = {
	/** Seconds before a change reaches the other replicas. */
	refreshSeconds: number;
	items: Setting[];
};

interface SettingShape {
	key: string;
	category: string;
	display_name: string;
	description: string;
	input_type: string;
	effective: string;
	stored_value: string | null;
	source: string;
	default_value: string;
}

export async function getSettings(): Promise<Settings> {
	const r = await api<{ refresh_seconds: number; items: SettingShape[] }>('/admin/settings');
	return {
		refreshSeconds: r.refresh_seconds,
		items: r.items.map((s) => ({
			key: s.key,
			category: s.category,
			displayName: s.display_name,
			description: s.description,
			inputType: s.input_type,
			effective: s.effective,
			storedValue: s.stored_value,
			source: s.source,
			defaultValue: s.default_value
		}))
	};
}

export async function setSetting(key: string, value: string): Promise<void> {
	await api(`/admin/settings/${encodeURIComponent(key)}`, {
		method: 'PUT',
		body: JSON.stringify({ value })
	});
}

/** Hand the setting back to configuration — which is not always the built-in default. */
export async function resetSetting(key: string): Promise<void> {
	await api(`/admin/settings/${encodeURIComponent(key)}`, { method: 'DELETE' });
}
