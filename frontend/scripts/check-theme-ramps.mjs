// Guards the one invariant the theme engine cannot check for itself.
//
// The engine derives every theme from a hardcoded copy of the shipped OKLCH
// ramps (see the header of src/lib/stores/theme.svelte.ts for why it is not read
// back from computed style). That copy has to stay identical to app.css: if a
// stop drifts, applying a theme silently changes the lightness the palette was
// contrast-checked at, and nothing else in the build would notice.
//
// The style settings brought a second copy with exactly the same failure mode:
// roundness multiplies the shipped radii, so a theme sitting at 1x has to come
// out at the values app.css ships. If app.css moves --srf-radius and the engine's
// table does not, applying any theme with a roundness override quietly resizes
// every corner in the app.
//
// Run with `npm run check:theme`. No dependencies — plain node.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const src = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
const css = readFileSync(join(src, 'app.css'), 'utf8');
const ts = readFileSync(join(src, 'lib', 'stores', 'theme.svelte.ts'), 'utf8');

const FAMILIES = ['primary', 'secondary', 'tertiary', 'success', 'warning', 'error', 'surface'];
const STOPS = [50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 950];

const fromCss = {};
for (const m of css.matchAll(
	/--color-(primary|secondary|tertiary|success|warning|error|surface)-(\d+):\s*oklch\(([\d.]+)\s+([\d.]+)\s+([\d.]+)\)/g
)) {
	(fromCss[m[1]] ??= {})[m[2]] = [parseFloat(m[3]), parseFloat(m[4])];
}

function literalAfter(marker, endMarker) {
	const block = ts.slice(ts.indexOf(marker), ts.indexOf(endMarker));
	const body = block.match(/\{[\s\S]*\}/);
	if (!body) throw new Error(`could not read the ${marker} literal — has the file moved?`);
	return eval(`(${body[0]})`);
}

const ramps = literalAfter('const RAMPS', '// The app shell sits');
const shellTokens = literalAfter('const SHELL', '// Chroma scaling');

const problems = [];

for (const family of FAMILIES) {
	for (const [i, stop] of STOPS.entries()) {
		const inCss = fromCss[family]?.[stop];
		const inTs = ramps[family]?.[i];
		if (!inCss) problems.push(`app.css has no --color-${family}-${stop}`);
		else if (!inTs) problems.push(`theme.svelte.ts has no ${family}[${i}] (stop ${stop})`);
		else if (inCss[0] !== inTs[0] || inCss[1] !== inTs[1])
			problems.push(
				`${family}-${stop}: app.css [${inCss}] vs theme.svelte.ts [${inTs}]`
			);
	}
}

// The shell literals live behind var() fallbacks so a surface tint can move them.
// The fallback must still be the value the engine assumes.
const shellPatterns = {
	'--app-bg-light': /--app-bg:\s*var\(--app-bg-light,\s*oklch\(([\d.]+)\s+([\d.]+)\s+([\d.]+)\)\)/,
	'--app-bg-dark': /--app-bg:\s*var\(--app-bg-dark,\s*oklch\(([\d.]+)\s+([\d.]+)\s+([\d.]+)\)\)/,
	'--ctl-bg-dark': /--ctl-bg:\s*var\(--ctl-bg-dark,\s*oklch\(([\d.]+)\s+([\d.]+)\s+([\d.]+)\)\)/
};

for (const [name, pattern] of Object.entries(shellPatterns)) {
	const m = css.match(pattern);
	const inTs = shellTokens[name];
	if (!m) problems.push(`app.css no longer declares ${name} as a var() fallback`);
	else if (!inTs) problems.push(`theme.svelte.ts has no SHELL entry for ${name}`);
	else {
		const [l, c, h] = [parseFloat(m[1]), parseFloat(m[2]), parseFloat(m[3])];
		if (l !== inTs.l || c !== inTs.c || h !== inTs.h)
			problems.push(
				`${name}: app.css [${l} ${c} ${h}] vs theme.svelte.ts [${inTs.l} ${inTs.c} ${inTs.h}]`
			);
	}
}

// ── radii ──────────────────────────────────────────────────────────────
// Only the tokens app.css actually owns. Tailwind's --radius-* scale is defined
// by the framework, not here, so there is nothing local to check it against.
const radii = literalAfter('const RADIUS_TOKENS', 'const clamp');

for (const token of ['srf-radius', 'ctl-radius', 'btn-radius', 'radius-base', 'radius-container']) {
	const m = css.match(new RegExp(String.raw`--${token}:\s*([\d.]+)rem`));
	const inTs = radii[token];
	if (!m) problems.push(`app.css no longer declares --${token} in rem`);
	else if (inTs === undefined)
		problems.push(`theme.svelte.ts has no RADIUS_TOKENS entry for --${token}`);
	else if (parseFloat(m[1]) !== inTs)
		problems.push(`--${token}: app.css ${m[1]}rem vs theme.svelte.ts ${inTs}rem`);
}

if (problems.length > 0) {
	console.error('Theme baseline has drifted from app.css:\n');
	for (const p of problems) console.error(`  · ${p}`);
	console.error('\nUpdate RAMPS/SHELL/RADIUS_TOKENS in src/lib/stores/theme.svelte.ts to match app.css.');
	process.exit(1);
}

console.log(
	`Theme baseline OK — ${FAMILIES.length} families x ${STOPS.length} stops + ` +
		`${Object.keys(shellPatterns).length} shell tokens + 5 radius tokens match app.css.`
);
