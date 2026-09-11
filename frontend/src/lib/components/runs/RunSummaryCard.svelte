<script lang="ts">
	// The first card of a run: what it is, how it ended, what started it.
	//
	// Takes a list row as readily as the full detail so the page can paint this the
	// instant it opens — the list already knew everything on this card — and let the
	// steps arrive underneath.
	import type { FleetRun } from '$lib/api/fleet.api';
	import { Card, Badge, StatusBadge } from '$lib/components/ui';
	import { absolute, timeAgo } from '$lib/utils/time';
	import { outcome, runDuration, shortId, triggerLabel } from './format';
	import { prefetchFleetRun } from '$lib/api/runs.api';

	let { run }: { run: FleetRun } = $props();

	const o = $derived(outcome(run));
	// A refusal shows as failed with 0 of 0 nodes, which reads like a platform bug
	// rather than a run that was stopped on purpose.
	const refused = $derived(Boolean(run.error) && run.nodeCount === 0);
	const deleted = $derived(run.workflowName === '(deleted)');
</script>

<Card>
	<div class="grid grid-cols-2 gap-4 md:grid-cols-4">
		<div>
			<div class="mb-1.5 text-xs text-surface-600-400">Status</div>
			<div class="flex flex-wrap items-center gap-1.5">
				<StatusBadge status={run.status} />
				{#if o}<Badge tone={o.tone}>{o.label}</Badge>{/if}
				{#if refused}<Badge tone="warning">refused</Badge>{/if}
			</div>
		</div>
		<div>
			<div class="mb-1.5 text-xs text-surface-600-400">Final state</div>
			<div class="text-sm text-surface-900-100">{run.finalState || '—'}</div>
		</div>
		<div>
			<div class="mb-1.5 text-xs text-surface-600-400">Started by</div>
			<div class="text-sm text-surface-900-100">{triggerLabel(run.trigger)}</div>
		</div>
		<div>
			<div class="mb-1.5 text-xs text-surface-600-400">Environment</div>
			<div class="text-sm text-surface-900-100">{run.environment || '—'}</div>
		</div>
		<div>
			<div class="mb-1.5 text-xs text-surface-600-400">Started</div>
			<div class="text-sm text-surface-900-100" title={absolute(run.startedAt)}>
				{absolute(run.startedAt)}
			</div>
			<div class="text-[11px] text-surface-600-400">{timeAgo(run.startedAt)}</div>
		</div>
		<div>
			<div class="mb-1.5 text-xs text-surface-600-400">Duration</div>
			<div
				class="font-mono text-sm tabular-nums text-surface-900-100"
				class:text-primary-700-300={run.durationSeconds === null}
			>
				{runDuration(run.durationSeconds)}
			</div>
		</div>
		<div>
			<div class="mb-1.5 text-xs text-surface-600-400">Changed</div>
			<div class="text-sm tabular-nums text-surface-900-100">
				{run.changedCount}<span class="text-surface-600-400"> / {run.nodeCount} steps</span>
			</div>
		</div>
		<div>
			<div class="mb-1.5 text-xs text-surface-600-400">Failed</div>
			<div
				class="text-sm tabular-nums"
				class:text-error-700-300={run.failedCount > 0}
				class:text-surface-900-100={run.failedCount === 0}
			>
				{run.failedCount}
			</div>
		</div>
	</div>

	<!-- A run refused before it began has no steps to explain it, so this is the only
	     place the reason can appear. -->
	{#if run.error}
		<div class="mt-4 rounded-lg border border-error-500/40 bg-error-500/10 px-3 py-2">
			<div class="text-xs font-medium text-error-700-300">
				{run.nodeCount === 0 ? 'Nothing ran' : 'The run itself failed'}
			</div>
			<p class="mt-0.5 text-xs text-error-700-300">{run.error}</p>
		</div>
	{/if}

	<div
		class="mt-4 flex flex-wrap items-center gap-x-6 gap-y-1 border-t border-surface-200-800 pt-4 text-xs"
	>
		<span>
			<span class="text-surface-600-400">Workflow:</span>
			{#if deleted}
				<span class="ml-2 text-surface-600-400">(deleted)</span>
			{:else}
				<a
					href={`/workflows/${run.workflowId}`}
					class="ml-2 font-medium text-primary-700-300 hover:underline">{run.workflowName}</a
				>
			{/if}
		</span>
		<span>
			<span class="text-surface-600-400">Run id:</span>
			<span class="ml-2 font-mono text-surface-700-300">{run.workflowRunId}</span>
		</span>
		<!-- A child run is half a story on its own: it was started by a step in another
		     run, and that run is where the reason it exists lives. -->
		{#if run.parentRunId}
			<span>
				<span class="text-surface-600-400">Parent run:</span>
				<a
					href={`/runs/${run.parentRunId}`}
					class="ml-2 font-mono text-primary-700-300 hover:underline"
					onmouseenter={() => prefetchFleetRun(run.parentRunId!)}
					onfocus={() => prefetchFleetRun(run.parentRunId!)}>{shortId(run.parentRunId)}…</a
				>
			</span>
		{/if}
	</div>
</Card>
