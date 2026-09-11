// One run, opened from the fleet view (`/api/runs/{id}`, `/api/runs/{id}/steps/{n}`).
//
// The per-workflow detail in workflows.api.ts (`getRun`) returns every step with its
// payloads inline. That is the right call for the card shown the moment after a run
// was started, and the wrong one for a page reached from a list: a per-device node
// stores a payload per device, so the first paint waited on megabytes nobody had
// asked to see yet. Here the run arrives with step headers only, and each card
// fetches its own payload when it is opened.
//
// Everything is cached in memory. A finished run is immutable, so a second visit —
// back from the workflow, back from a step — should cost nothing; and the list
// prefetches on hover so that by the time the click lands the detail is already here.

import { api } from '$lib/api/client';
import type { FleetRun } from '$lib/api/fleet.api';

export type FleetStep = {
	nodeId: string;
	sequence: number;
	/** changed | no_change | failed | skipped */
	result: string;
	errorCode: string | null;
	retryable: boolean;
	/** The failure message. Null unless the step failed. */
	error: string | null;
	/** 0 when the step never reached a handler, 1 normally, more when retried. */
	attempts: number;
	startedAt: string | null;
	finishedAt: string | null;
	durationMs: number | null;
	/** Sizes, in characters, of what a payload fetch would bring back. 0 = nothing. */
	outputChars: number;
	inputChars: number;
	logsChars: number;
	/**
	 * The run a `subflow` step started; null on every other step. This endpoint carries
	 * step headers without their payloads on purpose, and a subflow step is the one step
	 * whose detail is not a payload at all — it is another run. Without this the card is
	 * a dead end: you can see that a child workflow ran and not what it did.
	 */
	childRunId: string | null;
};

export type FleetRunDetail = FleetRun & {
	/** The payload the run was triggered with. Null when it had none. */
	input: unknown;
	/** Device ids the run targeted. Empty means no device context. */
	targetDevices: string[];
	steps: FleetStep[];
};

export type FleetStepPayload = {
	nodeId: string;
	sequence: number;
	output: unknown;
	input: unknown;
	logs: string | null;
};

interface StepShape {
	node_id: string;
	sequence: number;
	result: string;
	error_code: string | null;
	retryable: boolean;
	error: string | null;
	attempts: number;
	started_at: string | null;
	finished_at: string | null;
	duration_ms: number | null;
	output_chars: number;
	input_chars: number;
	logs_chars: number;
	child_run_id?: string | null;
}

interface DetailShape {
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
	input?: unknown;
	target_devices?: unknown;
	steps?: StepShape[];
}

interface PayloadShape {
	node_id: string;
	sequence: number;
	output?: unknown;
	input?: unknown;
	logs?: string | null;
}

function toDetail(r: DetailShape): FleetRunDetail {
	return {
		workflowRunId: r.workflow_run_id,
		workflowId: r.workflow_id,
		workflowName: r.workflow_name,
		environment: r.environment,
		status: r.status,
		finalState: r.final_state,
		nodeCount: r.node_count,
		changedCount: r.changed_count,
		failedCount: r.failed_count,
		startedAt: r.started_at,
		finishedAt: r.finished_at,
		durationSeconds: r.duration_seconds,
		trigger: r.trigger ?? 'manual',
		error: r.error ?? null,
		parentRunId: r.parent_run_id ?? null,
		input: r.input ?? null,
		targetDevices: Array.isArray(r.target_devices)
			? r.target_devices.filter((d): d is string => typeof d === 'string')
			: [],
		steps: (r.steps ?? []).map((s) => ({
			nodeId: s.node_id,
			sequence: s.sequence,
			result: s.result,
			errorCode: s.error_code ?? null,
			retryable: s.retryable,
			error: s.error ?? null,
			attempts: s.attempts ?? 0,
			startedAt: s.started_at ?? null,
			finishedAt: s.finished_at ?? null,
			durationMs: s.duration_ms ?? null,
			outputChars: s.output_chars ?? 0,
			inputChars: s.input_chars ?? 0,
			logsChars: s.logs_chars ?? 0,
			childRunId: s.child_run_id ?? null
		}))
	};
}

// ── caches ──────────────────────────────────────────────────────────────

// Bounded and insertion-ordered: Map iterates oldest-first, so evicting the first
// key is a cheap LRU once entries are re-inserted on every hit.
const LIMIT = 40;

function remember<K, V>(map: Map<K, V>, key: K, value: V): void {
	if (map.has(key)) map.delete(key);
	map.set(key, value);
	if (map.size > LIMIT) {
		const oldest = map.keys().next();
		if (!oldest.done) map.delete(oldest.value);
	}
}

/** Rows the list already had. Lets the detail page paint its header before the fetch. */
const summaries = new Map<string, FleetRun>();
/** Finished runs only — the one kind that cannot change under us. */
const details = new Map<string, FleetRunDetail>();
/** Single-flight: a hover prefetch and the click that follows share one request. */
const inflight = new Map<string, Promise<FleetRunDetail>>();
const payloads = new Map<string, Promise<FleetStepPayload>>();

export function rememberFleetRuns(rows: FleetRun[]): void {
	for (const r of rows) remember(summaries, r.workflowRunId, r);
}

/** What is known about a run right now, without a request. Null when nothing is. */
export function peekFleetRun(id: string): FleetRun | FleetRunDetail | null {
	return details.get(id) ?? summaries.get(id) ?? null;
}

export function getFleetRun(id: string, opts: { fresh?: boolean } = {}): Promise<FleetRunDetail> {
	if (!opts.fresh) {
		const hit = details.get(id);
		if (hit) return Promise.resolve(hit);
		const pending = inflight.get(id);
		if (pending) return pending;
	} else {
		// A refresh must also drop the payloads, or a re-read would mix a fresh run
		// with step bodies from before.
		for (const key of [...payloads.keys()]) if (key.startsWith(`${id}:`)) payloads.delete(key);
	}

	const request = api<DetailShape>(`/runs/${id}`)
		.then(toDetail)
		.then((d) => {
			if (d.finishedAt) remember(details, id, d);
			remember(summaries, id, d);
			return d;
		})
		.finally(() => {
			if (inflight.get(id) === request) inflight.delete(id);
		});
	inflight.set(id, request);
	return request;
}

/** Fire-and-forget warm-up for a hover. Failures are the click's to report, not the hover's. */
export function prefetchFleetRun(id: string): void {
	void getFleetRun(id).catch(() => {});
}

export function getFleetRunStep(runId: string, sequence: number): Promise<FleetStepPayload> {
	const key = `${runId}:${sequence}`;
	const cached = payloads.get(key);
	if (cached) return cached;

	const request = api<PayloadShape>(`/runs/${runId}/steps/${sequence}`)
		.then((p) => ({
			nodeId: p.node_id,
			sequence: p.sequence,
			output: p.output ?? null,
			input: p.input ?? null,
			logs: p.logs ?? null
		}))
		.catch((e) => {
			// A failed fetch must not be remembered as the answer.
			payloads.delete(key);
			throw e;
		});
	remember(payloads, key, request);
	return request;
}
