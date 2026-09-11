// Keeps the SPA's idea of the deployment's capabilities in agreement with the
// backend's, and holds the fail-closed rule to its promise.
//
// Two different failures, one script:
//
//   1. Drift. The module keys are a wire contract — the manifest sends them, the
//      navigation registry classifies destinations with them. A key added to the
//      backend catalog and not here is a capability the UI can never offer; one
//      removed there and left here is a page that stays hidden forever, and neither
//      shows up as a type error because both sides are just strings.
//
//   2. The case nobody exercises by hand. "The manifest failed to load" is the one
//      state a developer never sees, and the one where assuming everything is on
//      fills the menu with pages that 503.
//
// Run with `npm run check:modules`. No dependencies — plain node, with type
// stripping, which is why manifest.ts imports nothing.

import { readdirSync, readFileSync as read, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, relative } from 'node:path';

import { MODULE_IDS, ModuleAvailability, parseManifest } from '../src/lib/modules/manifest.ts';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

// Line endings are a checkout detail: a Windows working copy is CRLF, and the
// anchored patterns below would match nothing without this.
const readFileSync = (path, encoding) => read(path, encoding).replace(/\r\n/g, '\n');
const problems = [];

// ── the module keys, against the backend catalog ────────────────────────
const catalog = readFileSync(
	join(root, '..', 'nashira_backend', 'Configuration', 'Modules', 'ModuleCatalog.cs'),
	'utf8'
);

// Define(ModuleId.Chat, "chat", …) — the second argument is the public key.
const backendIds = [...catalog.matchAll(/Define\(\s*ModuleId\.\w+,\s*"([a-z0-9-]+)"/g)].map(
	(m) => m[1]
);

if (backendIds.length === 0)
	problems.push('ModuleCatalog.cs: no module keys found — has the shape changed?');

for (const id of backendIds)
	if (!MODULE_IDS.includes(id)) problems.push(`backend declares module "${id}", MODULE_IDS does not`);

for (const id of MODULE_IDS)
	if (!backendIds.includes(id)) problems.push(`MODULE_IDS declares "${id}", the backend catalog does not`);

// ── nothing decides a module at build time ──────────────────────────────
// One frontend image has to serve every combination, so the answer can only come
// from the manifest at runtime. A build-time variable would bake one deployment's
// selection into the bundle and be wrong everywhere else — silently, because the
// wrong pages would simply be missing.
function sourceFiles(dir) {
	return readdirSync(dir).flatMap((entry) => {
		const full = join(dir, entry);
		if (statSync(full).isDirectory()) return sourceFiles(full);
		return /\.(ts|js|svelte)$/.test(entry) ? [full] : [];
	});
}

// The mechanism, not the word: prose may name NASHIRA_MODULES (this file does), but
// nothing may read a module selection out of the environment.
const buildTimeRead = /(?:import\.meta\.env|process\.env)\s*(?:\.|\[\s*['"])\s*[A-Za-z_]*MODULE/;

for (const file of sourceFiles(join(root, 'src'))) {
	if (buildTimeRead.test(readFileSync(file, 'utf8')))
		problems.push(
			`${relative(root, file)} reads a module selection from the environment — it is a ` +
				'deployment variable the SPA learns through GET /api/modules, never at build time'
		);
}

// ── the manifest the backend actually sends ─────────────────────────────
const allEnabled = {
	configuration_mode: 'default_all',
	modules: backendIds.map((id) => ({
		id,
		enabled: true,
		configurable: id !== 'core',
		dependencies: []
	}))
};

const minimal = {
	configuration_mode: 'explicit',
	modules: backendIds.map((id) => ({
		id,
		enabled: id === 'core',
		configurable: id !== 'core',
		dependencies: []
	}))
};

function check(what, condition) {
	if (!condition) problems.push(what);
}

const all = ModuleAvailability.from(parseManifest(allEnabled));
check('all-enabled: configuration mode is not read back', all.configurationMode === 'default_all');
check('all-enabled: not every module is enabled', all.enabledIds.length === backendIds.length);
check('all-enabled: chat should be enabled', all.isEnabled('chat'));
check('all-enabled: a combined requirement should pass', all.areEnabled(['automation', 'fleet']));

const small = ModuleAvailability.from(parseManifest(minimal));
check('minimal: core should be enabled', small.isEnabled('core'));
check('minimal: chat should not be enabled', !small.isEnabled('chat'));
check('minimal: exactly core should be enabled', small.enabledIds.join(',') === 'core');
check(
	'minimal: a requirement with one half enabled must fail',
	!small.areEnabled(['core', 'automation'])
);

// A capability the manifest does not mention is not a capability, whatever it is
// called: an id the SPA cannot route to must not reach the derived surfaces.
const withStranger = ModuleAvailability.from(
	parseManifest({
		configuration_mode: 'explicit',
		modules: [
			{ id: 'core', enabled: true, configurable: false, dependencies: [] },
			{ id: 'time-travel', enabled: true, configurable: true, dependencies: [] }
		]
	})
);
check(
	'unknown module keys must be dropped',
	withStranger.enabledIds.join(',') === 'core' && withStranger.modules.length === 1
);

// ── the failure nobody exercises by hand ────────────────────────────────
for (const [name, payload] of [
	['null', null],
	['empty object', {}],
	['modules not an array', { configuration_mode: 'explicit', modules: 'all' }],
	['invalid configuration mode', { configuration_mode: 'surprise', modules: allEnabled.modules }],
	[
		'core reported disabled',
		{
			configuration_mode: 'explicit',
			modules: [{ id: 'core', enabled: false, configurable: false, dependencies: [] }]
		}
	]
]) {
	let threw = false;
	try {
		parseManifest(payload);
	} catch {
		threw = true;
	}
	check(`a malformed manifest (${name}) must be rejected, not guessed at`, threw);
}

const closed = ModuleAvailability.failClosed();
check('fail-closed: core must stay reachable', closed.isEnabled('core'));
check('fail-closed: nothing but core', closed.enabledIds.join(',') === 'core');
for (const id of MODULE_IDS.filter((id) => id !== 'core'))
	check(`fail-closed: ${id} must not be assumed enabled`, !closed.isEnabled(id));

// ── report ──────────────────────────────────────────────────────────────
if (problems.length > 0) {
	console.error('check:modules failed\n');
	for (const problem of problems) console.error(`  - ${problem}`);
	process.exit(1);
}

console.log(`check:modules ok — ${MODULE_IDS.length} modules, manifest and fail-closed behaviour`);
