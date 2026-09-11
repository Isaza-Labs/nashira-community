// Keeps the navigation registry, the actual routes and the docs in agreement.
//
// These three drifted once already: `/api/python-modules` shipped with a full
// CRUD API and no screen, while the docs advertised a `uiPath` that never had it.
// The registry made the sidebar/palette/hub consistent with each other; this
// script is what keeps them consistent with the filesystem and the docs.
//
// Run with `npm run check:nav`. No dependencies — plain node.

import { readFileSync as read, existsSync, readdirSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const src = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');

// Line endings are a checkout detail, not a source one: git hands Windows working
// copies CRLF, and every anchored pattern below would quietly match nothing.
const readFileSync = (path, encoding) => read(path, encoding).replace(/\r\n/g, '\n');

const problems = [];

// ── the registry, read as text (no TS runtime here) ─────────────────────
const registry = readFileSync(join(src, 'lib', 'nav', 'registry.ts'), 'utf8');

const hrefs = [...registry.matchAll(/^\t\thref: '([^']+)'/gm)].map((m) => m[1]);
if (hrefs.length === 0) problems.push('registry.ts: no page hrefs found — has the shape changed?');

// Every declared destination must be a real route. A route may serve its own
// path through an optional parameter (`chat/[[conversationId]]`), so that counts.
function routeExists(href) {
	const dir = join(src, 'routes', href.replace(/^\//, ''));
	if (existsSync(join(dir, '+page.svelte'))) return true;
	if (!existsSync(dir)) return false;
	return readdirSync(dir).some(
		(child) => child.startsWith('[[') && existsSync(join(dir, child, '+page.svelte'))
	);
}

for (const href of hrefs) {
	if (!routeExists(href))
		problems.push(`registry declares ${href} but no matching +page.svelte exists under src/routes`);
}

// Every sidebar/section reference must name a declared page or section. Scoped to
// the SECTIONS block: sidebar groups use the same `id:`/`label:` shape, and
// counting those would let a group id pass as a section id.
const sectionsBlock = registry.slice(
	registry.indexOf('export const SECTIONS'),
	registry.indexOf('export const SIDEBAR')
);
const sectionIds = [...sectionsBlock.matchAll(/^\t\tid: '([a-z-]+)'/gm)].map((m) => m[1]);
for (const m of registry.matchAll(/\{ kind: '(page|section)', id: '([^']+)' \}/g)) {
	const [, kind, id] = m;
	if (kind === 'page' && !hrefs.includes(id))
		problems.push(`sidebar references page '${id}', which is not in PAGES`);
	if (kind === 'section' && !sectionIds.includes(id))
		problems.push(`sidebar references section '${id}', which is not in SECTIONS`);
}

// Every page claiming a section must name one that exists.
for (const m of registry.matchAll(/^\t\tsection: '([^']+)'/gm)) {
	if (!sectionIds.includes(m[1])) problems.push(`a page claims section '${m[1]}', which does not exist`);
}

// ── every destination declares the capability it belongs to ─────────────
// A page without a module would be a screen that appears in deployments which never
// bought the capability behind it — and, unlike a missing route, nothing at runtime
// would say so. TypeScript already requires the field; this checks that what it holds
// is a real module key, and that the routes outside the registry are classified too.
const manifestSource = readFileSync(join(src, 'lib', 'modules', 'manifest.ts'), 'utf8');
const moduleIds = [
	...manifestSource
		.slice(manifestSource.indexOf('export const MODULE_IDS'))
		.matchAll(/^\t'([a-z0-9-]+)',?$/gm)
].map((m) => m[1]);

if (moduleIds.length === 0)
	problems.push('manifest.ts: no MODULE_IDS found — has the shape changed?');

// Pages, in order: each href is followed by its module inside the same object.
const pageModules = new Map();
{
	let href = null;
	for (const line of registry.split('\n')) {
		const h = line.match(/^\t\thref: '([^']+)',$/);
		if (h) href = h[1];
		const m = line.match(/^\t\tmodule: '([^']+)',$/);
		if (m && href) {
			pageModules.set(href, m[1]);
			href = null;
		}
	}
}

for (const href of hrefs) {
	const declared = pageModules.get(href);
	if (!declared) problems.push(`registry: ${href} declares no module`);
	else if (!moduleIds.includes(declared))
		problems.push(`registry: ${href} declares module '${declared}', which is not a module key`);
}

// Routes that exist without being destinations — redirects, the hub, public entrances.
const specialBlock = registry.slice(
	registry.indexOf('const SPECIAL_ROUTES'),
	registry.indexOf('export function moduleForPath')
);
const specialRoutes = new Map(
	[...specialBlock.matchAll(/^\t'([^']+)': '([a-z0-9-]+)'/gm)].map((m) => [m[1], m[2]])
);

for (const [route, declared] of specialRoutes)
	if (!moduleIds.includes(declared))
		problems.push(`SPECIAL_ROUTES: ${route} declares module '${declared}', which is not a module key`);

// And the reverse of the check above: every route on disk is classified by one of the
// two tables. A new page added without a module reaches this and stops the build.
function routePaths(dir, prefix = '') {
	const out = [];
	for (const entry of readdirSync(dir)) {
		const full = join(dir, entry);
		if (!statSync(full).isDirectory()) continue;
		// Route groups (name) do not appear in the URL.
		const segment = entry.startsWith('(') ? '' : `/${entry}`;
		if (existsSync(join(full, '+page.svelte'))) out.push(`${prefix}${segment}` || '/');
		out.push(...routePaths(full, `${prefix}${segment}`));
	}
	return out;
}

const routesDir = join(src, 'routes');
const allRoutes = [
	...(existsSync(join(routesDir, '+page.svelte')) ? ['/'] : []),
	...routePaths(routesDir)
];

for (const route of allRoutes) {
	if (specialRoutes.has(route)) continue;
	// A dynamic child belongs to the destination it hangs off, the way the sidebar
	// and the tab bar already treat it.
	if (hrefs.some((href) => route === href || route.startsWith(`${href}/`))) continue;
	problems.push(
		`route ${route} has no module: add it to PAGES with a module, or to SPECIAL_ROUTES`
	);
}

// ── docs: every section is classified, and uiPath points at a real screen ─
const docsDir = join(src, 'lib', 'docs', 'sections');
for (const file of readdirSync(docsDir).filter((f) => f.endsWith('.ts'))) {
	const content = readFileSync(join(docsDir, file), 'utf8');

	// Same reasoning as the pages: documentation for a capability the deployment does
	// not run reads as a missing feature rather than as one nobody bought.
	let slug = null;
	for (const line of content.split('\n')) {
		const s = line.match(/^\t\tslug: '([^']+)',$/);
		if (s) slug = s[1];
		const m = line.match(/^\t\tmodule: '([^']+)',$/);
		if (m && slug) {
			if (!moduleIds.includes(m[1]))
				problems.push(`docs/sections/${file}: '${slug}' declares module '${m[1]}', which is not a module key`);
			slug = null;
		}
	}
	const declaredSlugs = [...content.matchAll(/^\t\tslug: '([^']+)',$/gm)].length;
	const declaredModules = [...content.matchAll(/^\t\tmodule: '([^']+)',$/gm)].length;
	if (declaredSlugs !== declaredModules)
		problems.push(
			`docs/sections/${file}: ${declaredSlugs} sections but ${declaredModules} modules — ` +
				'every section declares the capability it documents'
		);

	for (const m of content.matchAll(/uiPath: '([^']+)'/g)) {
		const path = m[1];
		// Paths with a placeholder describe a detail screen, e.g. /workflows/{id}.
		if (path.includes('{')) continue;
		if (!path.startsWith('/')) continue;
		if (!hrefs.includes(path))
			problems.push(
				`docs/sections/${file}: uiPath '${path}' is not a registry destination ` +
					`(a section documenting a screen should point at one that exists)`
			);
	}
}

// ── report ──────────────────────────────────────────────────────────────
if (problems.length > 0) {
	console.error('Navigation is out of sync:\n');
	for (const p of problems) console.error(`  · ${p}`);
	console.error('\nFix src/lib/nav/registry.ts, the route, or the docs uiPath.');
	process.exit(1);
}

console.log(
	`Navigation OK — ${hrefs.length} destinations, ${sectionIds.length} tabbed surfaces, ` +
		`${allRoutes.length} routes classified across ${moduleIds.length} modules, ` +
		`and all docs uiPath values resolve.`
);
