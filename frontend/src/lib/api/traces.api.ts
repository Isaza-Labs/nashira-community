// The operational trail (`/api/admin/traces`, Admin). Read-only.
//
// `durationMs === null` on a row means the operation started and has not reported an
// outcome — work that is in flight, or stuck. That is a state, not missing data, and
// the screen must not render it as a zero.

import { api } from '$lib/api/client';

export type TraceEvent = {
	traceEventId: string;
	category: string;
	action: string;
	status: 'started' | 'completed' | 'failed' | string;
	durationMs: number | null;
	actor: string | null;
	userId: string | null;
	/** Shared with the audit trail, so one request joins across both. */
	requestId: string | null;
	errorMessage: string | null;
	metadataJson: string | null;
	at: string;
};

export type TraceList = {
	total: number;
	limit: number;
	offset: number;
	/** Which order the server returned: `at` normally, `duration` once slower-than is set. */
	sortedBy: 'at' | 'duration';
	items: TraceEvent[];
};

export type TraceSummary = {
	minutes: number;
	from: string;
	total: number;
	byCategory: Record<string, number>;
	byStatus: Record<string, number>;
	/** Started and never closed. */
	inFlight: number;
	failed: number;
	slowest: TraceEvent[];
};

export type TraceFilters = {
	/**
	 * One free-text term matched against every text column a trace carries — action,
	 * category, error message, request id, actor and the metadata blob. Not a prefix
	 * match: people arrive here with a fragment, not the head of a dotted name.
	 */
	search?: string;
	category?: string;
	status?: string;
	/** Who fired it: user id, part of a username, part of an email, or the actor string. */
	user?: string;
	/** Exact match, for the deep link out of a row. `search` would match too loosely. */
	requestId?: string;
	minDurationMs?: number;
	/** ISO instants. The list is bounded by the same window the summary cards report. */
	from?: string;
	to?: string;
	limit?: number;
	offset?: number;
};

interface TraceShape {
	trace_event_id: string;
	category: string;
	action: string;
	status: string;
	duration_ms: number | null;
	actor: string | null;
	user_id: string | null;
	request_id: string | null;
	error_message: string | null;
	metadata_json: string | null;
	at: string;
}

function toTrace(t: TraceShape): TraceEvent {
	return {
		traceEventId: t.trace_event_id,
		category: t.category,
		action: t.action,
		status: t.status,
		durationMs: t.duration_ms,
		actor: t.actor,
		userId: t.user_id,
		requestId: t.request_id,
		errorMessage: t.error_message,
		metadataJson: t.metadata_json,
		at: t.at
	};
}

export async function getTraces(f: TraceFilters = {}): Promise<TraceList> {
	const q = new URLSearchParams();
	if (f.search) q.set('search', f.search);
	if (f.category) q.set('category', f.category);
	if (f.status) q.set('status', f.status);
	if (f.user) q.set('user', f.user);
	if (f.requestId) q.set('request_id', f.requestId);
	if (f.minDurationMs) q.set('min_duration_ms', String(f.minDurationMs));
	if (f.from) q.set('from', f.from);
	if (f.to) q.set('to', f.to);
	q.set('limit', String(f.limit ?? 100));
	q.set('offset', String(f.offset ?? 0));

	const r = await api<{
		total: number;
		limit: number;
		offset: number;
		sorted_by?: string;
		items: TraceShape[];
	}>(`/admin/traces?${q}`);
	return {
		total: r.total,
		limit: r.limit,
		offset: r.offset,
		sortedBy: r.sorted_by === 'duration' ? 'duration' : 'at',
		items: r.items.map(toTrace)
	};
}

export async function getTraceSummary(minutes: number): Promise<TraceSummary> {
	const r = await api<{
		minutes: number;
		from: string;
		total: number;
		by_category: Record<string, number>;
		by_status: Record<string, number>;
		in_flight: number;
		failed: number;
		slowest: TraceShape[];
	}>(`/admin/traces/summary?minutes=${minutes}`);
	return {
		minutes: r.minutes,
		from: r.from,
		total: r.total,
		byCategory: r.by_category,
		byStatus: r.by_status,
		inFlight: r.in_flight,
		failed: r.failed,
		slowest: r.slowest.map(toTrace)
	};
}
