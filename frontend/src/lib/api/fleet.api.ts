// Runs and schedules across every workflow (`/api/runs`, `/api/schedules`).
//
// The per-workflow equivalents live in workflows.api.ts and answer a different
// question: what has *this one* done. These answer what happened, and what is about to.

import { api } from '$lib/api/client';

export type FleetRun = {
	workflowRunId: string;
	workflowId: string;
	/** "(deleted)" when the workflow is gone — the run still happened. */
	workflowName: string;
	environment: string;
	status: string;
	/** completed | rolled_back | failed — only the last left the world half-changed. */
	finalState: string;
	nodeCount: number;
	changedCount: number;
	failedCount: number;
	startedAt: string;
	finishedAt: string | null;
	/** Null while the run is still going, not zero. */
	durationSeconds: number | null;
	/** manual | agent | schedule | webhook | git_webhook | test | subflow. */
	trigger: string;
	/** Set only when the run never reached a step — a refusal, not a step failure. */
	error: string | null;
	/**
	 * The run whose `subflow` step started this one; null for a run started on its own
	 * account. `trigger: 'subflow'` already says a run is somebody's child — this says
	 * whose, so a child run is never a row with no explanation for existing.
	 */
	parentRunId: string | null;
};

export type FleetTrigger = {
	workflowTriggerId: string;
	workflowId: string;
	workflowName: string;
	workflowEnvironment: string;
	name: string;
	type: string;
	enabled: boolean;
	cronExpression: string | null;
	timezone: string;
	route: string | null;
	nextRunAt: string | null;
	lastRunAt: string | null;
	lastRunStatus: string | null;
	lastRunId: string | null;
	lastError: string | null;
	fireCount: number;
	/** Decided against the server's clock, not the browser's. */
	overdue: boolean;
};

export type Page<T> = { items: T[]; total: number; limit: number; offset: number };

export type RunFilters = {
	status?: string;
	environment?: string;
	finalState?: string;
	workflowId?: string;
	q?: string;
	limit?: number;
	offset?: number;
};

export type ScheduleFilters = {
	type?: string;
	enabled?: boolean;
	q?: string;
	dueWithinHours?: number;
	limit?: number;
	offset?: number;
};

interface RunShape {
	workflow_run_id: string;
	workflow_id: string;
	workflow_name: string;
	environment: string;
	status: string;
	final_state: string;
	node_count: number;
	changed_count: number;
	failed_count: number;
	started_at: string;
	finished_at: string | null;
	duration_seconds: number | null;
	trigger?: string;
	error?: string | null;
	parent_run_id?: string | null;
}

interface TriggerShape {
	workflow_trigger_id: string;
	workflow_id: string;
	workflow_name: string;
	workflow_environment: string;
	name: string;
	type: string;
	enabled: boolean;
	cron_expression: string | null;
	timezone: string;
	route: string | null;
	next_run_at: string | null;
	last_run_at: string | null;
	last_run_status: string | null;
	last_run_id: string | null;
	last_error: string | null;
	fire_count: number;
	overdue: boolean;
}

interface PageShape<T> {
	items: T[];
	total: number;
	limit: number;
	offset: number;
}

export async function listFleetRuns(f: RunFilters = {}): Promise<Page<FleetRun>> {
	const qs = new URLSearchParams();
	if (f.status) qs.set('status', f.status);
	if (f.environment) qs.set('environment', f.environment);
	if (f.finalState) qs.set('final_state', f.finalState);
	if (f.workflowId) qs.set('workflow_id', f.workflowId);
	if (f.q) qs.set('q', f.q);
	qs.set('limit', String(f.limit ?? 50));
	qs.set('offset', String(f.offset ?? 0));

	const r = await api<PageShape<RunShape>>(`/runs?${qs}`);
	return {
		total: r.total,
		limit: r.limit,
		offset: r.offset,
		items: r.items.map((x) => ({
			workflowRunId: x.workflow_run_id,
			workflowId: x.workflow_id,
			workflowName: x.workflow_name,
			environment: x.environment,
			status: x.status,
			finalState: x.final_state,
			nodeCount: x.node_count,
			changedCount: x.changed_count,
			failedCount: x.failed_count,
			startedAt: x.started_at,
			finishedAt: x.finished_at,
			durationSeconds: x.duration_seconds,
			trigger: x.trigger ?? 'manual',
			error: x.error ?? null,
			parentRunId: x.parent_run_id ?? null
		}))
	};
}

export async function listSchedules(f: ScheduleFilters = {}): Promise<Page<FleetTrigger>> {
	const qs = new URLSearchParams();
	if (f.type) qs.set('type', f.type);
	if (f.enabled !== undefined) qs.set('enabled', String(f.enabled));
	if (f.q) qs.set('q', f.q);
	if (f.dueWithinHours) qs.set('due_within_hours', String(f.dueWithinHours));
	qs.set('limit', String(f.limit ?? 100));
	qs.set('offset', String(f.offset ?? 0));

	const r = await api<PageShape<TriggerShape>>(`/schedules?${qs}`);
	return {
		total: r.total,
		limit: r.limit,
		offset: r.offset,
		items: r.items.map((x) => ({
			workflowTriggerId: x.workflow_trigger_id,
			workflowId: x.workflow_id,
			workflowName: x.workflow_name,
			workflowEnvironment: x.workflow_environment,
			name: x.name,
			type: x.type,
			enabled: x.enabled,
			cronExpression: x.cron_expression,
			timezone: x.timezone,
			route: x.route,
			nextRunAt: x.next_run_at,
			lastRunAt: x.last_run_at,
			lastRunStatus: x.last_run_status,
			lastRunId: x.last_run_id,
			lastError: x.last_error,
			fireCount: x.fire_count,
			overdue: x.overdue
		}))
	};
}
