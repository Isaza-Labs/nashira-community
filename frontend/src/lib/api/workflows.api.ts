// Canonical workflows: read the materialized plan (topological order + rollback
// safety) and the compiled YAML, run with confirmation, and read run status.
// Wire DTOs are snake_case (`[JsonPropertyName]`), mapped to idiomatic types here.
// Reads are Viewer; run is Operator (the API enforces it — the UI just gates the
// affordance).

import { api, apiText, type ListResponse } from '$lib/api/client';

export interface WorkflowSummary {
	id: string;
	name: string;
	version: number;
	environment: string;
	schemaHash: string;
	lastSimulationId: string | null;
	// The AI conversation this workflow was authored in; null when it was created
	// through the API or the UI rather than the chat.
	conversationId: string | null;
	updatedAt: string;
}

// A node as needed for the plan preview and for deriving what a run must ask for.
export interface WorkflowNode {
	id: string;
	snippetId: string;
	type: string;
	/**
	 * The node's config as authored, templates unresolved. Carried verbatim because
	 * this is where `{{ input.X }}` references live, and scanning them is the only way
	 * to know what a run has to ask for when no `input_schema` was declared.
	 */
	configOverrides: Record<string, unknown>;
}

export interface Workflow extends WorkflowSummary {
	description: string | null;
	schemaVersion: string;
	nodes: WorkflowNode[];
	/**
	 * The declared JSON Schema for `{{ input.* }}`, when the author wrote one. Null is
	 * the common case — see `effectiveInputSchema` in `$lib/workflow/deriveInputs`,
	 * which falls back to deriving one from the nodes.
	 */
	inputSchema: Record<string, unknown> | null;
	changeSummary: string;
	createdAt: string;
}

export interface WorkflowPlan {
	order: string[];
	rollbackReversible: boolean;
	nonReversibleNodes: string[];
	/**
	 * Nodes the plan could not score at all, with the reason. Always a subset of
	 * `nonReversibleNodes`: an unscorable node is scored at the ceiling so the plan
	 * never promises a rollback over work it could not read. Only `subflow` nodes reach
	 * this — a child workflow that is missing, cyclic, unreadable or past the depth cap.
	 */
	unresolvableNodes: { nodeId: string; code: string; reason: string }[];
}

export interface StepRun {
	nodeId: string;
	sequence: number;
	result: string;
	errorCode: string | null;
	retryable: boolean;
	/**
	 * What the node produced. Null when it produced nothing. On a read-only node
	 * this IS the result, and on a failed one it carries the message that
	 * `errorCode` only names.
	 */
	output: unknown;
	/** The failure message, in its own field rather than dug out of `output.error`. */
	error: string | null;
	/**
	 * The node's configuration as the handler received it: templates resolved,
	 * secret-looking values redacted, capped. A `{{ … }}` reference that resolved to
	 * something other than what the author assumed cannot be reconstructed after the
	 * run, which is what makes this the field worth reading first.
	 */
	input: unknown;
	/** The handler's own account of what it did — what explains a `no_change`. */
	logs: string | null;
	/**
	 * The run a `subflow` step started; null on every other step. The step's `output`
	 * carries the same id inside its JSON, but reading a link out of a payload is a
	 * convention, and this is the column the engine actually recorded.
	 */
	childRunId: string | null;
	/** 0 when the step never reached a handler, 1 normally, more when retried. */
	attempts: number;
	startedAt: string | null;
	finishedAt: string | null;
	durationMs: number | null;
}

export interface WorkflowRun {
	id: string;
	workflowId: string;
	environment: string;
	status: string;
	finalState: string;
	nodeCount: number;
	changedCount: number;
	failedCount: number;
	startedAt: string;
	finishedAt: string | null;
	/** The payload the run was triggered with, feeding `{{ input.* }}`. */
	input: unknown;
	/** Device ids the run targeted. Empty means it had no device context. */
	targetDevices: string[];
	/** manual | agent | schedule | webhook | git_webhook | test | subflow. */
	trigger: string;
	/**
	 * Why the run failed when no step can say — a policy denied it, the DAG would not
	 * parse. Null on success and on ordinary step failures.
	 */
	error: string | null;
	/** The run whose `subflow` step started this one; null for a run of its own. */
	parentRunId: string | null;
}

export interface WorkflowRunDetail extends WorkflowRun {
	steps: StepRun[];
}

/**
 * What an import created here, beyond the workflow itself. Only a bundle import fills
 * these: a plain YAML artifact carries no definitions, so there is nothing to create.
 */
export interface ImportedWorkflowResult extends WorkflowSummary {
	/**
	 * Every degradation and every thing the operator still has to configure — a secret
	 * that does not exist on this instance, a trigger waiting for its targets, a
	 * snippet that arrived with its network flag dropped. Empty means nothing was
	 * degraded; that silence is the contract, so it is worth surfacing.
	 */
	importNotes: string[];
	/** Snippets recreated from the definitions the bundle carried, unverified. */
	createdSnippets: { id: string; name: string; type: string }[];
	/** Sub-workflows recreated as drafts (not the ones reused as identical locals). */
	createdWorkflows: { id: string; name: string }[];
	/** Triggers created DISABLED, with a fresh secret and no target devices. */
	createdTriggers: { id: string; name: string; type: string; route: string | null }[];
}

interface WorkflowSummaryShape {
	workflow_id: string;
	name: string;
	version: number;
	environment: string;
	schema_hash: string;
	last_simulation_id: string | null;
	conversation_id: string | null;
	updated_at: string;
}

interface WorkflowShape extends WorkflowSummaryShape {
	description: string | null;
	schema_version: string;
	nodes: unknown;
	input_schema?: unknown;
	change_summary: string;
	created_at: string;
}

// Present on a bundle import only; absent (not empty) on every other response.
interface ImportShape extends WorkflowShape {
	import_notes?: string[] | null;
	created_snippets?: { snippet_id: string; name: string; type: string }[] | null;
	created_workflows?: { workflow_id: string; name: string }[] | null;
	created_triggers?:
		| { workflow_trigger_id: string; name: string; type: string; route: string | null }[]
		| null;
}

interface PlanShape {
	order: string[];
	rollback_reversible: boolean;
	non_reversible_nodes: string[];
	unresolvable_nodes?: { node_id: string; code: string; reason: string }[];
}

interface RunShape {
	workflow_run_id: string;
	workflow_id: string;
	environment: string;
	status: string;
	final_state: string;
	node_count: number;
	input?: unknown;
	target_devices?: unknown;
	changed_count: number;
	failed_count: number;
	started_at: string;
	finished_at: string | null;
	trigger?: string;
	error?: string | null;
	parent_run_id?: string | null;
}

interface StepShape {
	node_id: string;
	sequence: number;
	result: string;
	error_code: string | null;
	retryable: boolean;
	output?: unknown;
	error?: string | null;
	input?: unknown;
	logs?: string | null;
	child_run_id?: string | null;
	attempts?: number;
	started_at?: string | null;
	finished_at?: string | null;
	duration_ms?: number | null;
}

interface RunDetailShape extends RunShape {
	steps: StepShape[];
}

function toSummary(s: WorkflowSummaryShape): WorkflowSummary {
	return {
		id: s.workflow_id,
		name: s.name,
		version: s.version,
		environment: s.environment,
		schemaHash: s.schema_hash,
		lastSimulationId: s.last_simulation_id,
		conversationId: s.conversation_id,
		updatedAt: s.updated_at
	};
}

function toNodes(raw: unknown): WorkflowNode[] {
	if (!Array.isArray(raw)) return [];
	return raw
		.map((n) => {
			const o = (n ?? {}) as Record<string, unknown>;
			const cfg = o.config_overrides;
			return {
				id: typeof o.id === 'string' ? o.id : '',
				snippetId: typeof o.snippet_id === 'string' ? o.snippet_id : '',
				type: typeof o.type === 'string' ? o.type : 'task',
				configOverrides:
					cfg && typeof cfg === 'object' && !Array.isArray(cfg)
						? (cfg as Record<string, unknown>)
						: {}
			};
		})
		.filter((n) => n.id !== '');
}

// The API returns the stored schema verbatim. Anything that is not a JSON object —
// absent, null, an array a bad import left behind — is no schema at all, and saying
// so here keeps every consumer from re-deciding what a malformed one means.
function toSchema(raw: unknown): Record<string, unknown> | null {
	return raw && typeof raw === 'object' && !Array.isArray(raw)
		? (raw as Record<string, unknown>)
		: null;
}

function toRun(r: RunShape): WorkflowRun {
	return {
		id: r.workflow_run_id,
		workflowId: r.workflow_id,
		environment: r.environment,
		status: r.status,
		finalState: r.final_state,
		nodeCount: r.node_count,
		changedCount: r.changed_count,
		failedCount: r.failed_count,
		startedAt: r.started_at,
		finishedAt: r.finished_at,
		input: r.input ?? null,
		targetDevices: Array.isArray(r.target_devices)
			? r.target_devices.filter((d): d is string => typeof d === 'string')
			: [],
		trigger: r.trigger ?? 'manual',
		error: r.error ?? null,
		parentRunId: r.parent_run_id ?? null
	};
}

export async function listWorkflows(
	environment?: string,
	limit = 100,
	offset = 0
): Promise<ListResponse<WorkflowSummary>> {
	const qs = new URLSearchParams({ limit: String(limit), offset: String(offset) });
	if (environment) qs.set('environment', environment);
	const res = await api<ListResponse<WorkflowSummaryShape>>(`/workflows?${qs.toString()}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toSummary) };
}

export async function getWorkflow(id: string): Promise<Workflow> {
	const s = await api<WorkflowShape>(`/workflows/${id}`);
	return {
		...toSummary(s),
		description: s.description,
		schemaVersion: s.schema_version,
		nodes: toNodes(s.nodes),
		inputSchema: toSchema(s.input_schema),
		changeSummary: s.change_summary,
		createdAt: s.created_at
	};
}

export async function getPlan(id: string): Promise<WorkflowPlan> {
	const p = await api<PlanShape>(`/workflows/${id}/plan`);
	return {
		order: p.order,
		rollbackReversible: p.rollback_reversible,
		nonReversibleNodes: p.non_reversible_nodes,
		unresolvableNodes: (p.unresolvable_nodes ?? []).map((u) => ({
			nodeId: u.node_id,
			code: u.code,
			reason: u.reason
		}))
	};
}

export function getYaml(id: string): Promise<string> {
	return apiText(`/workflows/${id}/yaml`);
}

// Saves a workflow's YAML artifact as a file. The fetch goes through `apiText` so it
// carries the auth header — a plain <a download> to the API would hit it unauthenticated.
export async function downloadYaml(id: string, name: string, version: number, environment: string) {
	const yaml = await getYaml(id);
	const url = URL.createObjectURL(new Blob([yaml], { type: 'application/yaml' }));
	try {
		const a = document.createElement('a');
		a.href = url;
		a.download = `${slug(name) || 'workflow'}-v${version}-${environment}.yaml`;
		a.click();
	} finally {
		// Revoking synchronously after click() is safe: the browser has already
		// resolved the blob. Skipping it leaks the blob for the page's lifetime.
		URL.revokeObjectURL(url);
	}
}

export function getBundle(id: string): Promise<string> {
	return apiText(`/workflows/${id}/bundle?download=false`);
}

// Saves the portable bundle (workflow-v1-conformance/bundle/SPEC.md v3). Unlike the
// YAML artifact, this one survives crossing instances: it carries the definition of
// every snippet and sub-workflow the nodes name, the identity (never the credentials)
// of every integration, MCP server, credential and repository they reference, the
// workflow's triggers without their secrets, and a `requires` block the receiving
// instance checks before it creates anything.
export async function downloadBundle(id: string, name: string) {
	const json = await getBundle(id);
	const url = URL.createObjectURL(new Blob([json], { type: 'application/json' }));
	try {
		const a = document.createElement('a');
		a.href = url;
		a.download = `${slug(name) || 'workflow'}.bundle.json`;
		a.click();
	} finally {
		URL.revokeObjectURL(url);
	}
}

function slug(name: string): string {
	return name
		.toLowerCase()
		.replace(/[^a-z0-9]+/g, '-')
		.replace(/^-+|-+$/g, '')
		.slice(0, 60)
		.replace(/-+$/, '');
}

// Imports a workflow artifact: the plain YAML/JSON export, or a v3 bundle — the server
// picks the path from the document's own `kind` marker, so the caller passes the file
// through unchanged. Always lands in `draft` as a new workflow; the server ignores any
// id or environment in the file. A bundle that cannot run here is refused rather than
// half-imported, and the error names every missing dependency at once.
export async function importWorkflow(
	content: string,
	name?: string,
	changeSummary?: string
): Promise<ImportedWorkflowResult> {
	const res = await api<ImportShape>('/workflows/import', {
		method: 'POST',
		body: JSON.stringify({
			content,
			name: name?.trim() || null,
			change_summary: changeSummary?.trim() || null
		})
	});
	return {
		...toSummary(res),
		importNotes: res.import_notes ?? [],
		createdSnippets: (res.created_snippets ?? []).map((s) => ({
			id: s.snippet_id,
			name: s.name,
			type: s.type
		})),
		createdWorkflows: (res.created_workflows ?? []).map((w) => ({
			id: w.workflow_id,
			name: w.name
		})),
		createdTriggers: (res.created_triggers ?? []).map((t) => ({
			id: t.workflow_trigger_id,
			name: t.name,
			type: t.type,
			route: t.route
		}))
	};
}

// Soft-deletes a workflow. Operator; the API deactivates the row rather than dropping
// it, so its runs and audit trail stay readable. It accepts any environment — deleting a
// promoted copy is allowed and is exactly why the caller has to confirm with the
// environment in front of the user.
export async function deleteWorkflow(id: string): Promise<void> {
	await api<WorkflowShape>(`/workflows/${id}`, { method: 'DELETE' });
}

/**
 * What a manual run was asked to do. Both halves are optional and both are omitted
 * rather than sent empty: the API treats an absent `input` as "no input" and an
 * absent `target_devices` as "no device context", which is not the same as sending
 * `{}` or `[]` and is what every run did before this dialog existed.
 */
export interface RunWorkflowPayload {
	input?: Record<string, unknown>;
	targetDevices?: string[];
}

export async function runWorkflow(id: string, payload: RunWorkflowPayload = {}): Promise<WorkflowRun> {
	const body: Record<string, unknown> = {};
	if (payload.input && Object.keys(payload.input).length > 0) body.input = payload.input;
	if (payload.targetDevices && payload.targetDevices.length > 0) {
		body.target_devices = payload.targetDevices;
	}
	return toRun(
		await api<RunShape>(`/workflows/${id}/run`, { method: 'POST', body: JSON.stringify(body) })
	);
}

export async function listRuns(
	id: string,
	limit = 20,
	offset = 0
): Promise<ListResponse<WorkflowRun>> {
	const res = await api<ListResponse<RunShape>>(`/workflows/${id}/runs?limit=${limit}&offset=${offset}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toRun) };
}

export async function getRun(runId: string): Promise<WorkflowRunDetail> {
	const r = await api<RunDetailShape>(`/workflows/runs/${runId}`);
	return {
		...toRun(r),
		steps: (r.steps ?? []).map((s) => ({
			nodeId: s.node_id,
			sequence: s.sequence,
			result: s.result,
			errorCode: s.error_code,
			retryable: s.retryable,
			output: s.output ?? null,
			error: s.error ?? null,
			input: s.input ?? null,
			logs: s.logs ?? null,
			childRunId: s.child_run_id ?? null,
			attempts: s.attempts ?? 0,
			startedAt: s.started_at ?? null,
			finishedAt: s.finished_at ?? null,
			durationMs: s.duration_ms ?? null
		}))
	};
}
