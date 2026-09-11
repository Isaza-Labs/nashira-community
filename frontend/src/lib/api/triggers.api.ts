// Workflow triggers (`/api/workflows/{id}/triggers`) — cron schedules and inbound
// webhooks, plus acceptance tests and promoted versions for the same workflow.
//
// The webhook secret is returned exactly once, on create and on rotate. There is
// no endpoint that returns it afterwards, so the UI must surface it immediately or
// it is lost.

import { api, type ListResponse } from '$lib/api/client';

export type TriggerType = 'cron' | 'webhook';

export type WorkflowTrigger = {
	id: string;
	workflowId: string;
	name: string;
	type: TriggerType;
	description: string | null;
	cronExpression: string | null;
	timezone: string;
	route: string | null;
	hasSecret: boolean;
	allowUnsigned: boolean;
	allowTargetOverride: boolean;
	targetDevices: string[];
	inputDefaults: unknown;
	enabled: boolean;
	nextRunAt: string | null;
	lastRunAt: string | null;
	lastRunStatus: string | null;
	lastRunId: string | null;
	lastError: string | null;
	fireCount: number;
	// Only ever populated by create and rotate.
	secret: string | null;
	updatedAt: string;
};

export interface TriggerPayload {
	name: string;
	type: TriggerType;
	description: string;
	cronExpression: string;
	timezone: string;
	targetDevices: string[];
	// Already parsed. The editor is a schema-driven form, not a JSON textarea, so
	// there is no half-typed text for this layer to validate.
	inputDefaults: Record<string, unknown>;
	allowUnsigned: boolean;
	allowTargetOverride: boolean;
	enabled: boolean;
}

interface TriggerShape {
	workflow_trigger_id: string;
	workflow_id: string;
	name: string;
	type: TriggerType;
	description: string | null;
	cron_expression: string | null;
	timezone: string;
	route: string | null;
	has_secret: boolean;
	allow_unsigned: boolean;
	allow_target_override: boolean;
	target_devices: string[];
	input_defaults: unknown;
	enabled: boolean;
	next_run_at: string | null;
	last_run_at: string | null;
	last_run_status: string | null;
	last_run_id: string | null;
	last_error: string | null;
	fire_count: number;
	secret: string | null;
	updated_at: string;
}

function toTrigger(t: TriggerShape): WorkflowTrigger {
	return {
		id: t.workflow_trigger_id,
		workflowId: t.workflow_id,
		name: t.name,
		type: t.type,
		description: t.description,
		cronExpression: t.cron_expression,
		timezone: t.timezone,
		route: t.route,
		hasSecret: t.has_secret,
		allowUnsigned: t.allow_unsigned,
		allowTargetOverride: t.allow_target_override,
		targetDevices: t.target_devices ?? [],
		inputDefaults: t.input_defaults,
		enabled: t.enabled,
		nextRunAt: t.next_run_at,
		lastRunAt: t.last_run_at,
		lastRunStatus: t.last_run_status,
		lastRunId: t.last_run_id,
		lastError: t.last_error,
		fireCount: t.fire_count,
		secret: t.secret,
		updatedAt: t.updated_at
	};
}

function toBody(p: TriggerPayload) {
	return {
		name: p.name,
		type: p.type,
		description: p.description || null,
		cron_expression: p.cronExpression.trim() || null,
		timezone: p.timezone.trim() || 'UTC',
		target_devices: p.targetDevices,
		// Null, not `{}`: an empty object would be merged under every run's input as a
		// present-but-empty default, which is not the same as declaring none.
		input_defaults: Object.keys(p.inputDefaults).length > 0 ? p.inputDefaults : null,
		allow_unsigned: p.allowUnsigned,
		allow_target_override: p.allowTargetOverride,
		enabled: p.enabled
	};
}

export async function listTriggers(workflowId: string): Promise<ListResponse<WorkflowTrigger>> {
	const res = await api<ListResponse<TriggerShape>>(`/workflows/${workflowId}/triggers`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toTrigger) };
}

export async function createTrigger(workflowId: string, p: TriggerPayload): Promise<WorkflowTrigger> {
	return toTrigger(
		await api<TriggerShape>(`/workflows/${workflowId}/triggers`, {
			method: 'POST',
			body: JSON.stringify(toBody(p))
		})
	);
}

export async function updateTrigger(
	workflowId: string,
	id: string,
	p: TriggerPayload
): Promise<WorkflowTrigger> {
	return toTrigger(
		await api<TriggerShape>(`/workflows/${workflowId}/triggers/${id}`, {
			method: 'PUT',
			body: JSON.stringify(toBody(p))
		})
	);
}

export async function deleteTrigger(workflowId: string, id: string): Promise<void> {
	await api<TriggerShape>(`/workflows/${workflowId}/triggers/${id}`, { method: 'DELETE' });
}

export async function rotateSecret(workflowId: string, id: string): Promise<WorkflowTrigger> {
	return toTrigger(
		await api<TriggerShape>(`/workflows/${workflowId}/triggers/${id}/rotate-secret`, { method: 'POST' })
	);
}

// ── acceptance tests ────────────────────────────────────────────────

export type AcceptanceTest = {
	id: string;
	workflowId: string;
	name: string;
	description: string | null;
	input: unknown;
	targetDevices: string[];
	assertions: unknown;
	lastStatus: string | null;
	lastRunId: string | null;
	lastRunAt: string | null;
	lastFailures: string[];
	// False when the workflow was edited since the verdict was measured.
	resultIsCurrent: boolean;
	updatedAt: string;
};

export interface AcceptanceTestPayload {
	name: string;
	description: string;
	// Already parsed, same as a trigger's defaults. `assertions` stays raw text —
	// it is a list of matchers with no schema to drive a form.
	input: Record<string, unknown>;
	targetDevices: string[];
	assertions: string;
}

interface TestShape {
	workflow_acceptance_test_id: string;
	workflow_id: string;
	name: string;
	description: string | null;
	input: unknown;
	target_devices: string[];
	assertions: unknown;
	last_status: string | null;
	last_run_id: string | null;
	last_run_at: string | null;
	last_failures: string[];
	result_is_current: boolean;
	updated_at: string;
}

function toTest(t: TestShape): AcceptanceTest {
	return {
		id: t.workflow_acceptance_test_id,
		workflowId: t.workflow_id,
		name: t.name,
		description: t.description,
		input: t.input,
		targetDevices: t.target_devices ?? [],
		assertions: t.assertions,
		lastStatus: t.last_status,
		lastRunId: t.last_run_id,
		lastRunAt: t.last_run_at,
		lastFailures: t.last_failures ?? [],
		resultIsCurrent: t.result_is_current,
		updatedAt: t.updated_at
	};
}

export async function listTests(workflowId: string): Promise<ListResponse<AcceptanceTest>> {
	const res = await api<ListResponse<TestShape>>(`/workflows/${workflowId}/tests`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toTest) };
}

export async function createTest(workflowId: string, p: AcceptanceTestPayload): Promise<AcceptanceTest> {
	return toTest(
		await api<TestShape>(`/workflows/${workflowId}/tests`, {
			method: 'POST',
			body: JSON.stringify({
				name: p.name,
				description: p.description || null,
				input: Object.keys(p.input).length > 0 ? p.input : null,
				target_devices: p.targetDevices,
				assertions: p.assertions.trim() ? JSON.parse(p.assertions) : null
			})
		})
	);
}

export async function deleteTest(workflowId: string, id: string): Promise<void> {
	await api<TestShape>(`/workflows/${workflowId}/tests/${id}`, { method: 'DELETE' });
}

export async function runTest(
	workflowId: string,
	id: string
): Promise<{ status: string; runId: string | null; failures: string[] }> {
	const r = await api<{ status: string; run_id: string | null; failures: string[] }>(
		`/workflows/${workflowId}/tests/${id}/run`,
		{ method: 'POST' }
	);
	return { status: r.status, runId: r.run_id, failures: r.failures ?? [] };
}

// ── versions ────────────────────────────────────────────────────────

export type WorkflowVersionSummary = {
	id: string;
	version: number;
	environment: string;
	schemaHash: string;
	changeSummary: string;
	promotedAt: string;
	promotedBy: string;
};

export async function listVersions(workflowId: string): Promise<WorkflowVersionSummary[]> {
	const r = await api<{
		items: {
			workflow_version_id: string;
			version: number;
			environment: string;
			schema_hash: string;
			change_summary: string;
			promoted_at: string;
			promoted_by: string;
		}[];
	}>(`/workflows/${workflowId}/versions`);

	return r.items.map((v) => ({
		id: v.workflow_version_id,
		version: v.version,
		environment: v.environment,
		schemaHash: v.schema_hash,
		changeSummary: v.change_summary,
		promotedAt: v.promoted_at,
		promotedBy: v.promoted_by
	}));
}

// Restores a snapshot as a NEW draft — never in place. A rollback goes through the
// same promotion gate as any other change.
export async function restoreVersion(versionId: string): Promise<{ id: string; name: string }> {
	const r = await api<{ workflow_id: string; name: string }>(
		`/workflows/versions/${versionId}/restore`,
		{ method: 'POST' }
	);
	return { id: r.workflow_id, name: r.name };
}
