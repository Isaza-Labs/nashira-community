import type { ModuleId } from '$lib/modules/manifest';

// Content model for the in-app documentation at /docs.
//
// The docs are structured data rather than markdown files on purpose: every
// section documents an API surface, and a typed shape means a parameter table
// cannot silently lose its "required" column or a route its role. It also makes
// the whole corpus searchable without parsing prose.
//
// Inline text supports a deliberately tiny grammar — `code` and **bold** — so a
// section can emphasise a field name without pulling in a markdown renderer.

export type Role = 'Viewer' | 'Operator' | 'Admin' | 'Public';

export type Method = 'GET' | 'POST' | 'PUT' | 'DELETE';

/** One field of a request or response body. */
export interface Param {
	name: string;
	type: string;
	/** Required on create. Update endpoints are partial unless stated otherwise. */
	required?: boolean;
	/** Default applied by the server when the field is omitted. */
	default?: string;
	desc: string;
}

export interface Endpoint {
	method: Method;
	path: string;
	role: Role;
	desc: string;
}

export type Block =
	| { kind: 'prose'; text: string }
	| { kind: 'heading'; text: string }
	| { kind: 'list'; items: string[] }
	| { kind: 'steps'; items: string[] }
	| { kind: 'params'; title?: string; intro?: string; rows: Param[] }
	| { kind: 'endpoints'; title?: string; rows: Endpoint[] }
	| { kind: 'code'; caption?: string; text: string }
	| { kind: 'note'; tone: 'neutral' | 'primary' | 'warning' | 'error'; title?: string; text: string }
	| { kind: 'values'; title?: string; rows: { value: string; desc: string }[] };

export interface DocSection {
	/** URL segment under /docs. */
	slug: string;
	title: string;
	group: string;
	/**
	 * The capability this section documents. Required, so a new section cannot be
	 * written without answering it: documentation for a capability the deployment does
	 * not run reads as a missing feature rather than as one nobody bought.
	 */
	module: ModuleId;
	/** One line, shown on the index card and under the title. */
	tagline: string;
	/** Where this lives in the UI, when it has a screen. */
	uiPath?: string;
	/** Base REST path, when it has one. */
	apiBase?: string;
	/** Minimum role to reach the primary screen. */
	role?: Role;
	/** "What it's for" — the answer before the mechanics. */
	purpose: string;
	/** Extra words that should match this section in search. */
	keywords?: string;
	blocks: Block[];
}

/**
 * Escape HTML, then apply the inline grammar. Escaping first is what makes the
 * result safe to render with {@html}: no authored string can inject markup.
 */
export function inline(text: string): string {
	const escaped = text
		.replace(/&/g, '&amp;')
		.replace(/</g, '&lt;')
		.replace(/>/g, '&gt;')
		.replace(/"/g, '&quot;');

	return escaped
		.replace(
			/`([^`]+)`/g,
			'<code class="rounded bg-surface-200-800 px-1 py-0.5 font-mono text-[0.85em]">$1</code>'
		)
		.replace(/\*\*([^*]+)\*\*/g, '<strong class="font-semibold">$1</strong>');
}
