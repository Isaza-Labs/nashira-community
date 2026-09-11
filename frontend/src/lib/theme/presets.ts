// Starting points for the theme editor: curated palettes, the labels the editor
// puts on every knob, and the randomiser.
//
// Kept out of `$lib/stores/theme.svelte.ts` on purpose. That module is the
// engine — pure, SSR-safe, and the thing every preview and every apply goes
// through. This is editorial content that changes whenever someone has a better
// idea for a palette, and it should not be able to break the engine when it does.
//
// One thing worth knowing before adding a preset: the engine reads only the HUE
// and the CHROMA of each pick — it keeps the lightness app.css contrast-checked
// (see the header there). So the hexes below are written at roughly each
// family's own 500 lightness. That is not a requirement; it just means the
// swatch shown in the picker looks like what the preset will actually produce.

import {
	oklchToHex,
	SETTING_DEFAULTS,
	type FontBodyKey,
	type FontHeadingKey,
	type FontMonoKey,
	type ThemeFamily,
	type ThemeSettings
} from '$lib/stores/theme.svelte';

export const FAMILY_META: Record<ThemeFamily, { label: string; hint: string }> = {
	primary: { label: 'Primary', hint: 'Buttons, links, focus rings, active nav' },
	secondary: { label: 'Secondary', hint: 'Information, progress and focus signals' },
	tertiary: { label: 'Tertiary', hint: 'A warm accent, used sparingly' },
	success: { label: 'Success', hint: 'Healthy, passed, delivered' },
	warning: { label: 'Warning', hint: 'Degraded, stale, disarmed' },
	error: { label: 'Error', hint: 'Failed, unreachable, denied, destructive buttons' },
	surface: { label: 'Surface tint', hint: 'Backgrounds, cards, inputs, borders — the whole shell' }
};

// ── style setting labels ────────────────────────────────────────────────
// The default is spelled out in the option text because these selects have no
// "unset" entry: picking the default IS how an override is cleared.

export const FONT_BODY_LABEL: Record<FontBodyKey, string> = {
	plex: 'IBM Plex Sans (default)',
	system: 'System UI',
	serif: 'Serif',
	mono: 'IBM Plex Mono'
};

export const FONT_HEADING_LABEL: Record<FontHeadingKey, string> = {
	inherit: 'Same as body (default)',
	plex: 'IBM Plex Sans',
	system: 'System UI',
	serif: 'Serif',
	mono: 'IBM Plex Mono'
};

export const FONT_MONO_LABEL: Record<FontMonoKey, string> = {
	plex: 'IBM Plex Mono (default)',
	system: 'System monospace'
};

// Headings ship at 650, between Plex's Semibold and Bold — the variable face can
// hit it, so the list keeps the exact value instead of rounding people to 600.
export const HEADING_WEIGHTS = [400, 500, 600, 650, 700, 800, 900] as const;

// ── presets ─────────────────────────────────────────────────────────────

export interface ThemePreset {
	key: string;
	label: string;
	hint: string;
	/** Only the families the preset means to override; the rest stay stock. */
	colors: Partial<Record<ThemeFamily, string>>;
	settings?: ThemeSettings;
}

// The presets keep success/warning/error inside their conventional bands. In a
// network tool those three are not decoration — a run list is read at a glance
// for "healthy / degraded / down", and a palette that makes failures teal costs
// more than any amount of prettiness it buys back.
export const THEME_PRESETS: readonly ThemePreset[] = [
	{
		key: 'nashira',
		label: 'Nashira',
		hint: 'The shipped palette. Somewhere to come back to. (Reset restores the exact stock look — the shipped primary shifts hue across the ramp, which a single pick cannot.)',
		colors: {
			primary: '#1d4e89',
			secondary: '#00889b',
			tertiary: '#8e7450',
			success: '#3f9e5f',
			warning: '#c9822a',
			error: '#b72b19',
			surface: '#87888d'
		}
	},
	{
		key: 'midnight',
		label: 'Midnight',
		hint: 'Indigo and violet over a blue-leaning shell. Holds up on a wall display.',
		colors: {
			primary: '#506eb0',
			secondary: '#8459db',
			tertiary: '#008994',
			success: '#359658',
			warning: '#d38f00',
			error: '#d5303f',
			surface: '#717682'
		}
	},
	{
		key: 'ember',
		label: 'Ember',
		hint: 'Copper and terracotta on warm greys, with softer corners.',
		colors: {
			primary: '#a35a32',
			secondary: '#ca4442',
			tertiary: '#8f7700',
			success: '#479356',
			warning: '#d98b00',
			error: '#d5322f',
			surface: '#81736a'
		},
		settings: { roundness: 1.4 }
	},
	{
		key: 'moss',
		label: 'Moss',
		hint: 'Greens and olive. Success works harder here — check the status badges.',
		colors: {
			primary: '#3d8152',
			secondary: '#608719',
			tertiary: '#997200',
			success: '#409551',
			warning: '#d78c00',
			error: '#ce4036',
			surface: '#70796e'
		}
	},
	{
		key: 'orchid',
		label: 'Orchid',
		hint: 'Magenta and violet with a teal counterpoint.',
		colors: {
			primary: '#915a8c',
			secondary: '#8c56d6',
			tertiary: '#008994',
			success: '#439458',
			warning: '#d98b09',
			error: '#d03653',
			surface: '#7d727d'
		}
	},
	{
		key: 'graphite',
		label: 'Graphite',
		hint: 'Almost monochrome, square corners — colour only where status demands it.',
		colors: {
			primary: '#68717d',
			secondary: '#6e7a8b',
			tertiary: '#6e7a8b',
			success: '#4e925a',
			warning: '#d58d25',
			error: '#ce3f39',
			surface: '#73767b'
		},
		settings: { roundness: 0.35 }
	},
	{
		key: 'contrast',
		label: 'High contrast',
		hint: 'Deep saturated hues and heavier headings, for legibility-first setups.',
		colors: {
			primary: '#1f6fca',
			secondary: '#904be4',
			tertiary: '#008a90',
			success: '#139948',
			warning: '#d38f00',
			error: '#e20025',
			surface: '#72767c'
		},
		settings: { heading_weight: 750, roundness: 0.5 }
	}
];

// ── randomiser ──────────────────────────────────────────────────────────

const spread = (base: number, range: number) => base + (Math.random() * 2 - 1) * range;

const angularDistance = (a: number, b: number) => {
	const d = (((a - b) % 360) + 360) % 360;
	return d > 180 ? 360 - d : d;
};

/**
 * A random palette that is still a palette. One brand hue drives primary and the
 * surface tint, the two accents sit at deliberate offsets from it, and the
 * status families stay inside their conventional bands — so a surprise theme
 * still reads "ok / degraded / down" without having to be learned first.
 *
 * The green is not a free roll. app.css holds success about 50° off primary so
 * the two do not read alike as the low-opacity tints alerts and badges use, and
 * a brand hue landing in the greens would wipe that out; success is taken from
 * whichever end of the green band sits furthest from the brand hue instead.
 *
 * Lightness is fixed per family at that family's own 500. The engine discards it
 * anyway, and holding it steady means the swatches differ only in hue and
 * saturation, which is the part actually being rolled.
 */
export function randomThemeColors(): Record<ThemeFamily, string> {
	const brand = Math.random() * 360;
	const green = angularDistance(120, brand) > angularDistance(168, brand) ? 120 : 168;

	return {
		primary: oklchToHex(0.545, spread(0.1, 0.025), brand),
		secondary: oklchToHex(0.575, spread(0.17, 0.035), brand + spread(150, 45)),
		tertiary: oklchToHex(0.575, spread(0.16, 0.035), brand + spread(60, 25)),
		success: oklchToHex(0.6, spread(0.13, 0.015), green),
		warning: oklchToHex(0.7, spread(0.155, 0.015), spread(72, 8)),
		error: oklchToHex(0.575, spread(0.195, 0.02), spread(25, 8)),
		// A wide chroma roll here would tint the whole shell hard: the engine
		// already grants surface a far bigger chroma multiplier than the accents
		// (its absolute chroma is tiny), so the range it is fed has to be small.
		surface: oklchToHex(0.565, spread(0.016, 0.008), brand)
	};
}

/**
 * Only the settings that differ from the app defaults. A slider parked on the
 * default means "inherit app.css", not "pin today's default forever" — which is
 * also what keeps a theme's saved settings empty until someone actually moves
 * something.
 */
export function pruneSettings(settings: Required<ThemeSettings>): ThemeSettings {
	const out: Record<string, unknown> = {};
	for (const [key, value] of Object.entries(settings)) {
		if (value !== SETTING_DEFAULTS[key as keyof ThemeSettings]) out[key] = value;
	}
	return out as ThemeSettings;
}
