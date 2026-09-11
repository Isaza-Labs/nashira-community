// Snippet catalog (`/api/snippets`). A snippet is the reusable step a workflow
// node invokes by `snippet_id`. Reads are Viewer; writes are Operator.

import { api, type ListResponse } from '$lib/api/client';

export type Snippet = {
	id: string;
	name: string;
	slug: string;
	type: string;
	description: string | null;
	code: string | null;
	scriptLanguage: string | null;
	inputSchema: string | null;
	outputSchema: string | null;
	targetMode: string;
	timeoutSeconds: number;
	idempotency: string | null;
	// What the executor will actually apply after the handler's ceiling. Differs
	// from `idempotency` when a declaration is overruled, which the UI must show.
	effectiveIdempotency: string;
	// Whether a step running this snippet CHANGES anything — a different question
	// from idempotency, which asks whether it could be undone. No form here edits
	// it; it is carried so a DUPLICATE keeps it. Dropping it on a copy looks
	// harmless and then fails every step that uses the copy: for a python_snippet
	// null means "the author has not said", and the step refuses to guess.
	changesState: boolean | null;
	logicDiagramMermaid: string | null;
	// Admin-only to enable: it widens which allowlist modules a python_snippet
	// may import.
	networkEnabled: boolean;
	verified: boolean;
	isActive: boolean;
	createdAt: string;
	updatedAt: string;
};

export interface SnippetPayload {
	name: string;
	type: string;
	description: string;
	code: string;
	inputSchema: string;
	outputSchema: string;
	targetMode: string;
	timeoutSeconds: number;
	idempotency: string;
	logicDiagramMermaid: string;
	networkEnabled: boolean;
	// Optional because the editor form has no control for either. They exist so a
	// caller that already HAS a snippet — the duplicate action — can carry them
	// across. Sending them as null is a no-op on update (the API only applies the
	// fields a request actually sets), so the form's payloads are unaffected.
	scriptLanguage?: string | null;
	changesState?: boolean | null;
}

export const TARGET_MODES = ['once', 'per_device'] as const;
export const IDEMPOTENCY_TIERS = ['', 'idempotent', 'requires_compensation', 'non_reversible'] as const;

interface SnippetShape {
	snippet_id: string;
	name: string;
	slug: string;
	type: string;
	description: string | null;
	code: string | null;
	script_language: string | null;
	input_schema: string | null;
	output_schema: string | null;
	target_mode: string;
	timeout_seconds: number;
	idempotency: string | null;
	effective_idempotency: string;
	changes_state: boolean | null;
	logic_diagram_mermaid: string | null;
	network_enabled: boolean;
	verified: boolean;
	is_active: boolean;
	created_at: string;
	updated_at: string;
}

function toSnippet(s: SnippetShape): Snippet {
	return {
		id: s.snippet_id,
		name: s.name,
		slug: s.slug,
		type: s.type,
		description: s.description,
		code: s.code,
		scriptLanguage: s.script_language,
		inputSchema: s.input_schema,
		outputSchema: s.output_schema,
		targetMode: s.target_mode,
		timeoutSeconds: s.timeout_seconds,
		idempotency: s.idempotency,
		effectiveIdempotency: s.effective_idempotency,
		changesState: s.changes_state,
		logicDiagramMermaid: s.logic_diagram_mermaid,
		networkEnabled: s.network_enabled,
		verified: s.verified,
		isActive: s.is_active,
		createdAt: s.created_at,
		updatedAt: s.updated_at
	};
}

function toBody(p: SnippetPayload) {
	return {
		name: p.name,
		type: p.type,
		description: p.description || null,
		code: p.code || null,
		input_schema: p.inputSchema.trim() || null,
		output_schema: p.outputSchema.trim() || null,
		target_mode: p.targetMode,
		timeout_seconds: p.timeoutSeconds,
		// Empty means "use the handler's default", which is not the same as any
		// particular tier — so it goes as null, not as a string.
		idempotency: p.idempotency || null,
		// Undefined and null are the same request here — the API applies only the
		// fields present, so a form that never sets these leaves the stored row
		// alone and a duplicate carries them across.
		script_language: p.scriptLanguage ?? null,
		changes_state: p.changesState ?? null,
		logic_diagram_mermaid: p.logicDiagramMermaid.trim() || null,
		network_enabled: p.networkEnabled
	};
}

// Copies a snippet into a new row. The only way to start from a body that already
// works: the editor offers an empty form, so a seeded baseline — the paramiko SSH
// primitive above all — is otherwise reachable only by retyping it.
//
// `name` is the caller's because the API refuses a duplicate name outright
// (`snippet_name_taken`), and the slug is allocated server-side from it.
//
// `networkEnabled` overrides what the source carries. Turning it ON is an admin
// act, so an operator copying a network-enabled snippet needs a way to ask for an
// inert copy rather than being told the whole duplicate failed.
export async function duplicateSnippet(
	id: string,
	name: string,
	networkEnabled?: boolean
): Promise<Snippet> {
	const source = await getSnippet(id);
	return createSnippet({
		name,
		type: source.type,
		description: source.description ?? '',
		code: source.code ?? '',
		scriptLanguage: source.scriptLanguage,
		inputSchema: source.inputSchema ?? '',
		outputSchema: source.outputSchema ?? '',
		targetMode: source.targetMode,
		timeoutSeconds: source.timeoutSeconds,
		idempotency: source.idempotency ?? '',
		changesState: source.changesState,
		logicDiagramMermaid: source.logicDiagramMermaid ?? '',
		networkEnabled: networkEnabled ?? source.networkEnabled
	});
}

// The handler types this build can actually execute. Drives the type picker so a
// form cannot produce an unrunnable snippet.
export async function listSnippetTypes(): Promise<string[]> {
	return api<string[]>('/snippets/types');
}

export async function listSnippets(limit = 100, offset = 0): Promise<ListResponse<Snippet>> {
	const res = await api<ListResponse<SnippetShape>>(`/snippets?limit=${limit}&offset=${offset}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toSnippet) };
}

// A workflow node names its snippet by id and nothing else, so a caller holding a
// node has no way to reach the definition without this.
export async function getSnippet(id: string): Promise<Snippet> {
	return toSnippet(await api<SnippetShape>(`/snippets/${id}`));
}

export async function createSnippet(p: SnippetPayload): Promise<Snippet> {
	return toSnippet(await api<SnippetShape>('/snippets', { method: 'POST', body: JSON.stringify(toBody(p)) }));
}

export async function updateSnippet(id: string, p: SnippetPayload): Promise<Snippet> {
	return toSnippet(
		await api<SnippetShape>(`/snippets/${id}`, { method: 'PUT', body: JSON.stringify(toBody(p)) })
	);
}

export async function deleteSnippet(id: string): Promise<void> {
	await api<SnippetShape>(`/snippets/${id}`, { method: 'DELETE' });
}
