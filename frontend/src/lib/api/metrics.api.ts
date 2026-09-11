// Dashboard aggregates (`/api/admin/metrics`, Admin). Read-only.
//
// Windowed series always come back with one bucket per day in the range, quiet days
// included, so nothing here has to backfill gaps before drawing.

import { api } from '$lib/api/client';

export type DailyBucket = { date: string; counts: Record<string, number> };

export type TopFailingWorkflow = {
	workflowId: string;
	workflowName: string;
	failedCount: number;
};

export type RunMetrics = {
	days: number;
	from: string;
	series: DailyBucket[];
	/** rolled_back vs failed — only the second left the world half-changed. */
	finalStates: Record<string, number>;
	byEnvironment: Record<string, number>;
	topFailing: TopFailingWorkflow[];
};

export type AuthMetrics = {
	days: number;
	from: string;
	series: DailyBucket[];
	successes: number;
	failures: number;
	lockouts: number;
};

export type QueueMetrics = {
	byStatus: Record<string, number>;
	queued: number;
	claimed: number;
	/** Depth alone cannot tell a busy queue from a stuck one. */
	oldestQueuedAt: string | null;
	oldestQueuedSeconds: number | null;
};

export type DeviceMetrics = {
	total: number;
	byStatus: Record<string, number>;
	byVendor: Record<string, number>;
	bySite: Record<string, number>;
	fromInventory: number;
	manual: number;
	allowDraft: number;
	allowQa: number;
	allowProduction: number;
};

interface BucketShape {
	date: string;
	counts: Record<string, number>;
}

export async function getRunMetrics(days: number): Promise<RunMetrics> {
	const r = await api<{
		days: number;
		from: string;
		series: BucketShape[];
		final_states: Record<string, number>;
		by_environment: Record<string, number>;
		top_failing: { workflow_id: string; workflow_name: string; failed_count: number }[];
	}>(`/admin/metrics/runs?days=${days}`);
	return {
		days: r.days,
		from: r.from,
		series: r.series,
		finalStates: r.final_states,
		byEnvironment: r.by_environment,
		topFailing: r.top_failing.map((t) => ({
			workflowId: t.workflow_id,
			workflowName: t.workflow_name,
			failedCount: t.failed_count
		}))
	};
}

export async function getAuthMetrics(days: number): Promise<AuthMetrics> {
	const r = await api<{
		days: number;
		from: string;
		series: BucketShape[];
		successes: number;
		failures: number;
		lockouts: number;
	}>(`/admin/metrics/auth?days=${days}`);
	return { ...r };
}

export async function getQueueMetrics(): Promise<QueueMetrics> {
	const r = await api<{
		by_status: Record<string, number>;
		queued: number;
		claimed: number;
		oldest_queued_at: string | null;
		oldest_queued_seconds: number | null;
	}>('/admin/metrics/queue');
	return {
		byStatus: r.by_status,
		queued: r.queued,
		claimed: r.claimed,
		oldestQueuedAt: r.oldest_queued_at,
		oldestQueuedSeconds: r.oldest_queued_seconds
	};
}

export async function getDeviceMetrics(): Promise<DeviceMetrics> {
	const r = await api<{
		total: number;
		by_status: Record<string, number>;
		by_vendor: Record<string, number>;
		by_site: Record<string, number>;
		from_inventory: number;
		manual: number;
		allow_draft: number;
		allow_qa: number;
		allow_production: number;
	}>('/admin/metrics/devices');
	return {
		total: r.total,
		byStatus: r.by_status,
		byVendor: r.by_vendor,
		bySite: r.by_site,
		fromInventory: r.from_inventory,
		manual: r.manual,
		allowDraft: r.allow_draft,
		allowQa: r.allow_qa,
		allowProduction: r.allow_production
	};
}
