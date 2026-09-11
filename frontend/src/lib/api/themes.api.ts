// UI themes (`/api/themes`). Any authenticated user manages their own; sharing
// one with everybody — in either direction — is an admin act.
//
// `colors` is a map of color family → base hex ("primary": "#2596be"). The
// backend stores it opaquely; what the tokens mean is this frontend's contract,
// implemented by the theme engine (`$lib/stores/theme.svelte`).
//
// `settings` is the other half — roundness, interface scale, fonts, heading
// weight. That one the backend DOES validate, because those values end up inside
// font-family and border-radius declarations on every user's page as soon as a
// theme is shared. It still arrives here as a loose map and gets narrowed below:
// a row hand-edited in the database must degrade to "inherit", not crash a list.

import { api, type ListResponse } from '$lib/api/client';
import {
	FONT_BODY_KEYS,
	FONT_HEADING_KEYS,
	FONT_MONO_KEYS,
	type ThemeSettings
} from '$lib/stores/theme.svelte';

export type Theme = {
	id: string;
	name: string;
	description: string | null;
	colors: Record<string, string>;
	settings: ThemeSettings;
	isShared: boolean;
	isMine: boolean;
	updatedAt: string;
};

export interface ThemePayload {
	name: string;
	description: string;
	colors: Record<string, string>;
	settings: ThemeSettings;
	isShared: boolean;
}

interface ThemeShape {
	theme_id: string;
	name: string;
	description: string | null;
	colors: unknown;
	settings: unknown;
	is_shared: boolean;
	is_mine: boolean;
	updated_at: string;
}

// Narrows the API's loose settings map to the keys the engine knows, dropping
// anything else. The server rejects unknown keys on write, so this only fires
// for rows that predate a key or were edited outside the app — and dropping is
// right there: an unrecognised setting cannot be rendered, and refusing to show
// the theme at all would be a worse answer than showing it without the setting.
function toSettings(raw: unknown): ThemeSettings {
	if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
	const src = raw as Record<string, unknown>;
	const out: ThemeSettings = {};

	const number = (key: 'roundness' | 'ui_scale' | 'heading_weight') => {
		const v = src[key];
		if (typeof v === 'number' && Number.isFinite(v)) out[key] = v;
	};
	number('roundness');
	number('ui_scale');
	number('heading_weight');

	if ((FONT_BODY_KEYS as readonly unknown[]).includes(src.font_body))
		out.font_body = src.font_body as ThemeSettings['font_body'];
	if ((FONT_HEADING_KEYS as readonly unknown[]).includes(src.font_heading))
		out.font_heading = src.font_heading as ThemeSettings['font_heading'];
	if ((FONT_MONO_KEYS as readonly unknown[]).includes(src.font_mono))
		out.font_mono = src.font_mono as ThemeSettings['font_mono'];

	return out;
}

function toTheme(t: ThemeShape): Theme {
	const colors: Record<string, string> = {};
	if (t.colors && typeof t.colors === 'object' && !Array.isArray(t.colors)) {
		for (const [k, v] of Object.entries(t.colors as Record<string, unknown>)) {
			if (typeof v === 'string') colors[k] = v;
		}
	}
	return {
		id: t.theme_id,
		name: t.name,
		description: t.description,
		colors,
		settings: toSettings(t.settings),
		isShared: t.is_shared,
		isMine: t.is_mine,
		updatedAt: t.updated_at
	};
}

function toBody(p: ThemePayload) {
	return {
		name: p.name,
		description: p.description || null,
		colors: p.colors,
		// Always sent, never omitted: on update the map replaces wholesale, so
		// returning every knob to its default has to travel as {} for the
		// overrides to genuinely go away.
		settings: p.settings,
		is_shared: p.isShared
	};
}

export async function listThemes(): Promise<ListResponse<Theme>> {
	const res = await api<ListResponse<ThemeShape>>('/themes');
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toTheme) };
}

export async function createTheme(p: ThemePayload): Promise<Theme> {
	return toTheme(await api<ThemeShape>('/themes', { method: 'POST', body: JSON.stringify(toBody(p)) }));
}

export async function updateTheme(id: string, p: ThemePayload): Promise<Theme> {
	return toTheme(
		await api<ThemeShape>(`/themes/${id}`, { method: 'PUT', body: JSON.stringify(toBody(p)) })
	);
}

export async function deleteTheme(id: string): Promise<void> {
	await api<ThemeShape>(`/themes/${id}`, { method: 'DELETE' });
}
