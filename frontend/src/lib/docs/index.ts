// The documentation registry. One import surface for the /docs routes.

import type { ModuleAvailability } from '$lib/modules/manifest';
import type { DocSection } from './types';
import { platform } from './sections/platform';
import { operations } from './sections/operations';
import { workflows } from './sections/workflows';
import { governance } from './sections/governance';
import { integrations } from './sections/integrations';
import { administration } from './sections/administration';
import { consoleScreens } from './sections/console';

export type { DocSection, Block, Param, Endpoint, Role, Method } from './types';
export { inline } from './types';

/** Group order is the reading order: concepts, then daily work, then configuration. */
export const GROUP_ORDER = [
	'Getting started',
	'Operations',
	'Workflow platform',
	'Governance',
	'Integrations & AI',
	'Administration'
] as const;

export const SECTIONS: DocSection[] = [
	...platform,
	...operations,
	...workflows,
	...governance,
	...integrations,
	...administration,
	...consoleScreens
];

export function sectionBySlug(slug: string): DocSection | undefined {
	return SECTIONS.find((s) => s.slug === slug);
}

/**
 * The sections this deployment has any use for. Docs stays in core — every install
 * has it — but a page explaining how to register a device to someone whose
 * deployment runs no fleet is not help, it is a description of a product they were
 * not sold.
 */
export function availableSections(availability: ModuleAvailability): DocSection[] {
	return SECTIONS.filter((s) => availability.isEnabled(s.module));
}

export interface DocGroup {
	title: string;
	sections: DocSection[];
}

/** Sections bucketed by group, in reading order. Groups with no match are dropped. */
export function groupSections(sections: DocSection[] = SECTIONS): DocGroup[] {
	return GROUP_ORDER.map((title) => ({
		title,
		sections: sections.filter((s) => s.group === title)
	})).filter((g) => g.sections.length > 0);
}

/**
 * Search across everything a section says, not just its title — a reader looking
 * for "HMAC" should land on Triggers even though the word is only in a note.
 */
export function searchSections(query: string, sections: DocSection[] = SECTIONS): DocSection[] {
	const q = query.trim().toLowerCase();
	if (!q) return sections;

	return sections.filter((s) => haystack(s).includes(q));
}

const cache = new Map<string, string>();

function haystack(s: DocSection): string {
	const hit = cache.get(s.slug);
	if (hit !== undefined) return hit;

	const parts: string[] = [s.title, s.group, s.tagline, s.purpose, s.keywords ?? '', s.slug];
	for (const b of s.blocks) {
		switch (b.kind) {
			case 'prose':
			case 'heading':
				parts.push(b.text);
				break;
			case 'list':
			case 'steps':
				parts.push(b.items.join(' '));
				break;
			case 'params':
				parts.push(b.title ?? '', b.intro ?? '');
				parts.push(b.rows.map((r) => `${r.name} ${r.type} ${r.desc}`).join(' '));
				break;
			case 'endpoints':
				parts.push(b.title ?? '');
				parts.push(b.rows.map((r) => `${r.method} ${r.path} ${r.desc}`).join(' '));
				break;
			case 'values':
				parts.push(b.title ?? '');
				parts.push(b.rows.map((r) => `${r.value} ${r.desc}`).join(' '));
				break;
			case 'code':
				parts.push(b.caption ?? '', b.text);
				break;
			case 'note':
				parts.push(b.title ?? '', b.text);
				break;
		}
	}

	const built = parts.join(' ').toLowerCase();
	cache.set(s.slug, built);
	return built;
}
