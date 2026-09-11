// The theme engine: turns a theme's per-family base colors into full token ramps
// and applies them at runtime.
//
// How it works, and why this shape:
//
//   app.css defines every color family as an OKLCH ramp of stops 50–950 whose
//   LIGHTNESS values were contrast-checked against the real component pairings
//   (see the header comment there). A theme that replaced those stops with
//   arbitrary user colors would throw that engineering away.
//
//   So a theme picks one base color per family, and the engine keeps each stop's
//   original lightness, swaps in the picked HUE, and scales chroma moderately.
//   Contrast is dominated by lightness, so every AA pairing app.css guarantees
//   survives recoloring — a user can make Nashira purple, but not unreadable.
//
//   The baseline ramps are CONSTANTS here rather than read back from computed
//   style. Reading them at runtime made the whole engine depend on the stylesheet
//   having been parsed at the moment the module happened to boot: when it had
//   not, capture returned nothing, every ramp came back null, apply() removed the
//   variables instead of setting them, and previews rendered empty. Constants
//   make ramp() pure — it works during SSR, before first paint, and inside a
//   scoped preview. Keep them in sync with app.css; CSS remains the source of
//   truth for what SHIPS, this table is the source of truth for what a theme
//   DERIVES FROM.
//
//   A theme also carries optional STYLE SETTINGS — corner roundness, interface
//   scale, font stacks, heading weight. They ride the same mechanism as the
//   colours and follow the same rule: an absent key inherits app.css, so a
//   colours-only theme (every theme saved before settings existed) behaves
//   exactly as it always did. What they map ONTO is the adaptation work: the
//   tokens below are Nashira's own — `--srf-radius`, `--ctl-radius`,
//   `--btn-radius`, `--font-sans`, `--heading-font-weight` — not some upstream
//   framework's, because those are what this app's components actually read.
//
//   Overrides are set as inline custom properties on <html>. The stock tokens
//   live under `[data-theme='nashira']`, and an element's style attribute beats
//   any selector, so no stylesheet surgery is needed; `data-mode` (light/dark)
//   is orthogonal and keeps working because the mode pairs consume these same
//   vars. Reset = remove the inline properties, and the stylesheet shows through.

import { browser } from '$app/environment';

export const THEME_FAMILIES = [
	'primary',
	'secondary',
	'tertiary',
	'success',
	'warning',
	'error',
	'surface'
] as const;
export type ThemeFamily = (typeof THEME_FAMILIES)[number];

export type UiMode = 'light' | 'dark';

const STOPS = [50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 950] as const;
const KEY = 'nashira:theme';

type Oklch = { l: number; c: number; h: number };

// ── the shipped ramps, mirroring app.css ────────────────────────────────
// [lightness, chroma] per stop, in STOPS order. Hue is per family and only
// matters as the reference the picked hue replaces.
const RAMPS: Record<ThemeFamily, [number, number][]> = {
	primary: [
		[0.97, 0.02], [0.94, 0.04], [0.88, 0.07], [0.8, 0.095], [0.7006, 0.1219], [0.4234, 0.1112],
		[0.38, 0.1], [0.34, 0.088], [0.3, 0.075], [0.265, 0.06], [0.23, 0.046]
	],
	secondary: [
		[0.96, 0.018], [0.92, 0.035], [0.86, 0.06], [0.78, 0.085], [0.69, 0.105], [0.575, 0.115],
		[0.505, 0.1], [0.44, 0.088], [0.37, 0.073], [0.31, 0.058], [0.24, 0.042]
	],
	tertiary: [
		[0.96, 0.015], [0.92, 0.03], [0.864, 0.0425], [0.78, 0.055], [0.69, 0.06], [0.575, 0.06],
		[0.505, 0.055], [0.44, 0.05], [0.37, 0.042], [0.31, 0.035], [0.24, 0.026]
	],
	success: [
		[0.96, 0.03], [0.92, 0.06], [0.86, 0.1], [0.78, 0.13], [0.69, 0.147], [0.6, 0.128],
		[0.52, 0.111], [0.44, 0.094], [0.36, 0.077], [0.3, 0.064], [0.23, 0.049]
	],
	warning: [
		[0.97, 0.023], [0.94, 0.047], [0.89, 0.09], [0.83, 0.13], [0.77, 0.15], [0.7, 0.155],
		[0.61, 0.145], [0.51, 0.125], [0.42, 0.1], [0.34, 0.08], [0.26, 0.06]
	],
	error: [
		[0.96, 0.018], [0.92, 0.038], [0.86, 0.07], [0.8, 0.1], [0.7285, 0.1689], [0.5124, 0.1791],
		[0.455, 0.16], [0.4, 0.14], [0.345, 0.118], [0.295, 0.096], [0.24, 0.073]
	],
	surface: [
		[0.985, 0.004], [0.955, 0.006], [0.905, 0.009], [0.835, 0.011], [0.68, 0.011], [0.627, 0.008],
		[0.455, 0.01], [0.37, 0.009], [0.33, 0.008], [0.29, 0.006], [0.252, 0.002]
	]
};

// The app shell sits deliberately OFF the surface ramp (see app.css: a panel and
// the background it sits on must not match). Those literals are still surface-
// coloured, so a surface-tinted theme has to move them too — otherwise the two
// largest areas on screen stay stock and the theme reads as "nothing happened".
// Same rule as everywhere else: keep the lightness, swap the hue.
const SHELL: Record<string, Oklch> = {
	'--app-bg-light': { l: 0.9445, c: 0.0136, h: 277.06 },
	'--app-bg-dark': { l: 0.205, c: 0.005, h: 277 },
	'--ctl-bg-dark': { l: 0.29, c: 0.007, h: 277 }
};

// Chroma scaling is clamped: a near-gray pick must not wash a family out, and a
// neon pick must not blow past what the ramp was designed to carry. Surface gets
// a wider ceiling because its absolute chroma is tiny — 0.016 × 2.5 is still a
// tint, not a colour.
const CHROMA_SCALE_MIN = 0.2;
const CHROMA_SCALE_MAX: Record<ThemeFamily, number> = {
	primary: 1.6,
	secondary: 1.6,
	tertiary: 1.6,
	success: 1.6,
	warning: 1.6,
	error: 1.6,
	surface: 2.5
};

// ── color math ──────────────────────────────────────────────────────────

// sRGB hex → OKLCH, via Björn Ottosson's OKLab constants. Only hue and chroma
// are consumed — lightness always comes from the baseline ramp.
export function hexToOklch(hex: string): Oklch | null {
	const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
	if (!m) return null;

	const n = parseInt(m[1], 16);
	const toLinear = (v: number) => {
		const c = v / 255;
		return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
	};
	const r = toLinear((n >> 16) & 0xff);
	const g = toLinear((n >> 8) & 0xff);
	const b = toLinear(n & 0xff);

	const l_ = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
	const m_ = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
	const s_ = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);

	const L = 0.2104542553 * l_ + 0.793617785 * m_ - 0.0040720468 * s_;
	const a = 1.9779984951 * l_ - 2.428592205 * m_ + 0.4505937099 * s_;
	const bb = 0.0259040371 * l_ + 0.7827717662 * m_ - 0.808675766 * s_;

	const c = Math.sqrt(a * a + bb * bb);
	let h = (Math.atan2(bb, a) * 180) / Math.PI;
	if (h < 0) h += 360;

	return { l: L, c, h };
}

// The inverse trip. The engine never needs it — it emits oklch() straight into
// CSS — but <input type="color"> only speaks hex, so a generated palette has to
// come back through sRGB before the editor can show it.
function oklchToRgb(l: number, c: number, hDeg: number): [number, number, number] {
	const h = (hDeg * Math.PI) / 180;
	const a = c * Math.cos(h);
	const b = c * Math.sin(h);

	const l_ = (l + 0.3963377774 * a + 0.2158037573 * b) ** 3;
	const m_ = (l - 0.1055613458 * a - 0.0638541728 * b) ** 3;
	const s_ = (l - 0.0894841775 * a - 1.291485548 * b) ** 3;

	const toSrgb = (v: number) => (v <= 0.0031308 ? v * 12.92 : 1.055 * Math.pow(v, 1 / 2.4) - 0.055);
	return [
		toSrgb(4.0767416621 * l_ - 3.3077115913 * m_ + 0.2309699292 * s_),
		toSrgb(-1.2684380046 * l_ + 2.6097574011 * m_ - 0.3413193965 * s_),
		toSrgb(-0.0041960863 * l_ - 0.7034186147 * m_ + 1.707614701 * s_)
	];
}

/**
 * One OKLCH colour as `#rrggbb`, gamut-mapped. Chroma is walked down until the
 * colour fits in sRGB instead of clipping the channels: clipping shifts the hue
 * — a saturated red clipped at the top drifts orange — and a randomiser that
 * quietly changes the hue it just rolled is worse than no randomiser.
 */
export function oklchToHex(l: number, c: number, hDeg: number): string {
	const fits = (rgb: [number, number, number]) =>
		rgb.every((v) => v >= -0.0001 && v <= 1.0001);

	let rgb = oklchToRgb(l, c, hDeg);
	if (!fits(rgb)) {
		let lo = 0;
		let hi = c;
		rgb = oklchToRgb(l, 0, hDeg);
		for (let i = 0; i < 18; i++) {
			const mid = (lo + hi) / 2;
			const candidate = oklchToRgb(l, mid, hDeg);
			if (fits(candidate)) {
				rgb = candidate;
				lo = mid;
			} else {
				hi = mid;
			}
		}
	}

	const byte = (v: number) =>
		Math.round(Math.min(1, Math.max(0, v)) * 255)
			.toString(16)
			.padStart(2, '0');
	return `#${byte(rgb[0])}${byte(rgb[1])}${byte(rgb[2])}`;
}

function scaleFor(family: ThemeFamily, picked: Oklch): number {
	const reference = RAMPS[family][5][1]; // the 500 stop
	if (reference <= 0.001) return 1;
	return Math.min(CHROMA_SCALE_MAX[family], Math.max(CHROMA_SCALE_MIN, picked.c / reference));
}

/**
 * The recolored ramp for one family, as oklch() strings keyed by stop. Pure —
 * the editor preview, the theme cards and apply() all go through it, so what a
 * preview shows is by construction what applying would set.
 */
export function ramp(family: ThemeFamily, hex: string): Record<number, string> | null {
	const picked = hexToOklch(hex);
	if (!picked) return null;

	const scale = scaleFor(family, picked);
	const hue = picked.h.toFixed(2);
	const out: Record<number, string> = {};
	STOPS.forEach((stop, i) => {
		const [l, c] = RAMPS[family][i];
		out[stop] = `oklch(${l} ${(c * scale).toFixed(4)} ${hue})`;
	});
	return out;
}

/** The stock hex of a family's 500 stop — the picker's starting point. */
export function stockHex(family: ThemeFamily): string {
	return STOCK_HEX[family];
}

// Precomputed so the editor can offer "the current look" as the default pick
// without a round-trip through the DOM.
const STOCK_HEX: Record<ThemeFamily, string> = {
	primary: '#1d4e89',
	secondary: '#00889b',
	tertiary: '#8e7450',
	success: '#3f9e5f',
	warning: '#c9822a',
	error: '#b72b19',
	surface: '#87888d'
};

// ── style settings ──────────────────────────────────────────────────────
// The non-colour half of a theme. Every key is optional and an absent key means
// "inherit app.css", which is what lets this ship without touching a single
// theme anyone has already saved.
//
// The vocabularies are closed, and not for tidiness: once a theme is shared,
// these values are interpolated into `font-family` and `border-radius`
// declarations on every other user's page. A free-text font field would be a
// licence to make the whole company's browser fetch an arbitrary third-party
// asset. The backend enforces the same keys, ranges and choices
// (Services/Themes/ThemeSettingsValidator) — this is the mirror of that
// contract, not a second opinion on it.

export const FONT_BODY_KEYS = ['plex', 'system', 'serif', 'mono'] as const;
export const FONT_HEADING_KEYS = ['inherit', 'plex', 'system', 'serif', 'mono'] as const;
export const FONT_MONO_KEYS = ['plex', 'system'] as const;

export type FontBodyKey = (typeof FONT_BODY_KEYS)[number];
export type FontHeadingKey = (typeof FONT_HEADING_KEYS)[number];
export type FontMonoKey = (typeof FONT_MONO_KEYS)[number];

export interface ThemeSettings {
	/** Multiplier over every corner radius. 0 is square, 1 is stock, 2 is round. */
	roundness?: number;
	/** Root font-size multiplier — all rem-based sizing follows. 0.85–1.15. */
	ui_scale?: number;
	font_body?: FontBodyKey;
	font_heading?: FontHeadingKey;
	font_mono?: FontMonoKey;
	/** CSS font-weight for headings, 300–900. The shipped value is 650. */
	heading_weight?: number;
}

/** What each knob falls back to. Mirrors app.css; it does not define it. */
export const SETTING_DEFAULTS: Required<ThemeSettings> = {
	roundness: 1,
	ui_scale: 1,
	font_body: 'plex',
	font_heading: 'inherit',
	font_mono: 'plex',
	heading_weight: 650
};

// `plex` is the pair app.css bundles through @fontsource, so it is the only
// entry here that names a webfont — and it is already in the bundle, nothing new
// is fetched. Everything else is system-resident. There is deliberately no
// serif webfont: offering "IBM Plex Serif" would name a face the build does not
// ship, and the app has to keep working air-gapped.
const SANS_STACKS: Record<Exclude<FontHeadingKey, 'inherit'>, string> = {
	plex: "'IBM Plex Sans Variable', ui-sans-serif, system-ui, -apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
	system:
		"ui-sans-serif, system-ui, -apple-system, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif",
	serif: "Georgia, 'Iowan Old Style', 'Times New Roman', Times, serif",
	mono: "'IBM Plex Mono', ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace"
};

const MONO_STACKS: Record<FontMonoKey, string> = {
	plex: "'IBM Plex Mono', ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace",
	system: "ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, 'Liberation Mono', monospace"
};

// Every radius token in play, at its shipped value in rem. Two groups, and both
// are needed:
//
//   - Nashira's own shape tokens, from app.css. `.ui-surface`, `.ui-control` and
//     `.ui-raised` are what every panel, input and button is actually built
//     from, so nothing rounds without these. `--radius-base` / `--radius-container`
//     are Skeleton's, kept in step so its components don't drift from ours.
//   - Tailwind's scale. `rounded-lg` resolves to `var(--radius-lg)`, and the app
//     reaches for those utilities roughly three times as often as for the .ui-*
//     primitives. Scaling only Nashira's tokens would round the cards and leave
//     every badge, chip and menu exactly as square as before.
//
// Tailwind only emits the variables its used utilities need, so a few entries
// here are inert until someone writes `rounded-xs` — which is exactly when they
// should already be covered. `rounded-full` is deliberately absent: Tailwind
// compiles it to a literal, so pills stay pills at any roundness, which is what
// anyone would expect of a pill.
const RADIUS_TOKENS: Record<string, number> = {
	'srf-radius': 0.75,
	'ctl-radius': 0.5,
	'btn-radius': 0.5,
	'radius-base': 0.375,
	'radius-container': 0.625,
	'radius-xs': 0.125,
	'radius-sm': 0.25,
	'radius-md': 0.375,
	'radius-lg': 0.5,
	'radius-xl': 0.75,
	'radius-2xl': 1,
	'radius-3xl': 1.5,
	'radius-4xl': 2
};

const clamp = (v: number, min: number, max: number) => Math.min(max, Math.max(min, v));

/**
 * The declarations one theme's settings add on top of its colours — same shape
 * as `themeVariables` so the two merge into one map.
 *
 * `font-size` is the odd one out: a real property, not a custom property. It has
 * to sit on the element that rem resolves against, which for the live apply is
 * <html>. Values arriving from the API are re-checked here rather than trusted,
 * so a hand-edited row degrades to "inherit" instead of emitting nonsense CSS.
 */
export function settingsVariables(settings: ThemeSettings | undefined): Record<string, string> {
	const vars: Record<string, string> = {};
	if (!settings) return vars;

	if (typeof settings.roundness === 'number' && Number.isFinite(settings.roundness)) {
		const factor = clamp(settings.roundness, 0, 2);
		for (const [token, rem] of Object.entries(RADIUS_TOKENS)) {
			vars[`--${token}`] = `${Math.round(rem * factor * 1000) / 1000}rem`;
		}
	}
	if (typeof settings.ui_scale === 'number' && Number.isFinite(settings.ui_scale)) {
		vars['font-size'] = `calc(100% * ${clamp(settings.ui_scale, 0.85, 1.15)})`;
	}
	if (settings.font_body && settings.font_body in SANS_STACKS) {
		const stack = SANS_STACKS[settings.font_body];
		vars['--font-sans'] = stack;
		// app.css declares `--base-font-family: var(--font-sans)` up on <html>, so
		// it RESOLVES there — a scoped preview redefining --font-sans on a
		// descendant could never move it. Emitting the finished stack keeps the
		// preview and the real apply identical, exactly as the shell tokens do.
		vars['--base-font-family'] = stack;
	}
	if (
		settings.font_heading &&
		settings.font_heading !== 'inherit' &&
		settings.font_heading in SANS_STACKS
	) {
		vars['--heading-font-family'] = SANS_STACKS[settings.font_heading];
	}
	if (settings.font_mono && settings.font_mono in MONO_STACKS) {
		vars['--font-mono'] = MONO_STACKS[settings.font_mono];
	}
	if (typeof settings.heading_weight === 'number' && Number.isFinite(settings.heading_weight)) {
		vars['--heading-font-weight'] = String(Math.round(clamp(settings.heading_weight, 300, 900)));
	}
	return vars;
}

/**
 * Every custom property a theme sets, as name → value. Used both for the real
 * apply (on <html>) and for scoped previews (on a wrapper element), which is
 * what keeps the two honest with each other.
 *
 * `mode` resolves the shell colours for one mode. Omit it for the root apply:
 * there both modes are written as `--app-bg-light` / `--app-bg-dark`, and the
 * `[data-mode]` rules in app.css pick between them. A scoped preview cannot use
 * that indirection — `--app-bg` is computed up on <html>, so a descendant has to
 * be handed the resolved value.
 */
export function themeVariables(
	colors: Record<string, string>,
	mode?: UiMode,
	settings?: ThemeSettings
): Record<string, string> {
	const vars: Record<string, string> = { ...settingsVariables(settings) };

	for (const family of THEME_FAMILIES) {
		const hex = colors[family];
		if (!hex) continue;
		const built = ramp(family, hex);
		if (!built) continue;
		for (const [stop, value] of Object.entries(built)) vars[`--color-${family}-${stop}`] = value;
	}

	const surface = colors.surface ? hexToOklch(colors.surface) : null;
	if (surface) {
		const scale = scaleFor('surface', surface);
		const hue = surface.h.toFixed(2);
		for (const [name, base] of Object.entries(SHELL)) {
			vars[name] = `oklch(${base.l} ${(base.c * scale).toFixed(4)} ${hue})`;
		}
		if (mode) {
			// Scoped previews need the RESOLVED shell tokens. app.css declares
			// `--srf-bg: var(--color-surface-950)` inside a `[data-mode]` rule on
			// <html>, so its value is computed up there; overriding the ramp on a
			// descendant cannot reach back and change it. Hand the descendant the
			// finished values instead.
			const dark = mode === 'dark';
			const s = (stop: number) => vars[`--color-surface-${stop}`];
			const mix = (stop: number, pct: number) =>
				`color-mix(in oklab, ${s(stop)} ${pct}%, transparent)`;

			vars['--app-bg'] = vars[dark ? '--app-bg-dark' : '--app-bg-light'];
			vars['--srf-bg'] = dark ? s(950) : s(50);
			vars['--ctl-bg'] = dark ? vars['--ctl-bg-dark'] : s(50);
			vars['--srf-border-color'] = dark ? mix(50, 9) : mix(950, 10);
			vars['--ctl-border-color'] = dark ? mix(50, 13) : mix(950, 16);
		}
	}

	return vars;
}

/** The same map as an inline `style` string, for scoped previews. */
export function themeStyle(
	colors: Record<string, string>,
	mode: UiMode,
	settings?: ThemeSettings
): string {
	return Object.entries(themeVariables(colors, mode, settings))
		.map(([k, v]) => `${k}: ${v}`)
		.join('; ');
}

// Every property the engine may have set, so a reset or a theme switch clears
// what the previous theme owned rather than leaving half of it behind.
function ownedProperties(): string[] {
	const names: string[] = [];
	for (const family of THEME_FAMILIES) for (const stop of STOPS) names.push(`--color-${family}-${stop}`);
	for (const token of Object.keys(RADIUS_TOKENS)) names.push(`--${token}`);
	return [
		...names,
		...Object.keys(SHELL),
		// The settings side. `font-size` is a real property rather than a custom
		// one, and removeProperty takes either — but it has to be listed, or a
		// reset would leave the interface stuck at the last theme's scale.
		'font-size',
		'--font-sans',
		'--base-font-family',
		'--heading-font-family',
		'--font-mono',
		'--heading-font-weight'
	];
}

function readStored(): {
	id: string | null;
	colors: Record<string, string>;
	settings: ThemeSettings;
} | null {
	if (!browser) return null;
	try {
		const raw = localStorage.getItem(KEY);
		if (!raw) return null;
		const parsed = JSON.parse(raw);
		if (typeof parsed !== 'object' || parsed === null) return null;
		return {
			id: typeof parsed.id === 'string' ? parsed.id : null,
			colors: parsed.colors ?? {},
			// Absent for anything stored before settings existed, which is the
			// same as a theme that sets none.
			settings: parsed.settings ?? {}
		};
	} catch {
		return null;
	}
}

class ThemeEngine {
	activeId = $state<string | null>(null);
	/** The applied colours, so the UI can show what is live without re-reading storage. */
	activeColors = $state<Record<string, string>>({});
	/** The applied style settings, same reason. */
	activeSettings = $state<ThemeSettings>({});

	constructor() {
		const stored = readStored();
		// A theme with no colours but a settings override is still a theme worth
		// restoring — "just make the corners square" is a legitimate one.
		if (stored && (Object.keys(stored.colors).length > 0 || Object.keys(stored.settings).length > 0))
			this.apply(stored.id, stored.colors, stored.settings);
	}

	/** Kept for callers that want one family's ramp (preview chips). */
	ramp(family: ThemeFamily, hex: string): Record<number, string> | null {
		return ramp(family, hex);
	}

	apply(id: string | null, colors: Record<string, string>, settings: ThemeSettings = {}) {
		if (!browser) return;

		const vars = themeVariables(colors, undefined, settings);
		const root = document.documentElement.style;

		// Clear first, then set: switching from a theme that touched `warning` to
		// one that does not must give `warning` back to the stylesheet. Same for
		// the settings — a theme that shrinks the radii has to hand them back.
		for (const name of ownedProperties()) root.removeProperty(name);
		for (const [name, value] of Object.entries(vars)) root.setProperty(name, value);

		this.activeId = id;
		this.activeColors = { ...colors };
		this.activeSettings = { ...settings };
		try {
			localStorage.setItem(KEY, JSON.stringify({ id, colors, settings }));
		} catch {
			/* storage unavailable — the in-memory theme still applies */
		}
	}

	reset() {
		if (!browser) return;
		const root = document.documentElement.style;
		for (const name of ownedProperties()) root.removeProperty(name);
		this.activeId = null;
		this.activeColors = {};
		this.activeSettings = {};
		try {
			localStorage.removeItem(KEY);
		} catch {
			/* ignore */
		}
	}
}

export const themeEngine = new ThemeEngine();
