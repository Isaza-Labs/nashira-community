<script lang="ts">
	// One run, on its own page.
	//
	// The same cards Flow Weaver shows for a run: the verdict up top, the steps as a
	// chain, then one card per step. Reached from the fleet list and from the
	// workflow's Runs tab, both of which used to expand the run in place — twenty rows
	// below the click, and only after every step payload had arrived.
	//
	// Fast by construction rather than by hope: the header paints from what the list
	// already knew, the step headers come without their payloads, and a payload is
	// fetched only when its card is opened.
	import { untrack } from 'svelte';
	import { page } from '$app/state';
	import { getFleetRun, peekFleetRun, type FleetRunDetail } from '$lib/api/runs.api';
	import type { FleetRun } from '$lib/api/fleet.api';
	import {
		PageHeader,
		Card,
		Button,
		Badge,
		Skeleton,
		ErrorState,
		toast
	} from '$lib/components/ui';
	import RunSummaryCard from '$lib/components/runs/RunSummaryCard.svelte';
	import RunDataFlow from '$lib/components/runs/RunDataFlow.svelte';
	import RunStepCard from '$lib/components/runs/RunStepCard.svelte';
	import { pretty, shortId } from '$lib/components/runs/format';
	import { ArrowLeft, RefreshCw, Workflow as WorkflowIcon } from 'lucide-svelte';

	// The [id] route param is always present here; assert to drop `| undefined`.
	const id = $derived(page.params.id!);

	let detail = $state<FleetRunDetail | null>(null);
	let loading = $state(false);
	let error = $state<unknown>(null);
	let seq = 0;

	// What can be shown before the detail lands: the list row, if we came from one.
	const summary = $derived<FleetRun | null>(detail ?? peekFleetRun(id));

	async function load(runId: string, fresh = false) {
		const mine = ++seq;
		loading = true;
		error = null;
		try {
			const d = await getFleetRun(runId, { fresh });
			if (mine !== seq) return;
			detail = d;
		} catch (e) {
			if (mine !== seq) return;
			if (detail) toast.fromError(e, "Couldn't refresh the run");
			else error = e;
		} finally {
			if (mine === seq) loading = false;
		}
	}

	// Re-run on navigation between runs; a stale detail must not linger under the
	// new id while the next one loads.
	$effect(() => {
		const current = id;
		untrack(() => {
			detail = null;
			void load(current);
		});
	});

	const title = $derived(
		summary && summary.workflowName !== '(deleted)' ? summary.workflowName : `Run ${shortId(id)}`
	);

	const hasInput = $derived.by(() => {
		const v = detail?.input;
		if (v === null || v === undefined) return false;
		return !(typeof v === 'object' && !Array.isArray(v) && Object.keys(v as object).length === 0);
	});

	const skipped = $derived(detail?.steps.filter((s) => s.result === 'skipped').length ?? 0);
	const noChange = $derived(detail?.steps.filter((s) => s.result === 'no_change').length ?? 0);
	const nothingChanged = $derived(
		Boolean(detail) && detail!.changedCount === 0 && detail!.failedCount === 0 && detail!.nodeCount > 0
	);
</script>

<svelte:head><title>Run {shortId(id)} · Nashira</title></svelte:head>

<a
	href="/runs"
	class="mb-3 inline-flex items-center gap-1.5 text-sm text-surface-600-400 hover:text-surface-950-50"
>
	<ArrowLeft size={15} />Runs
</a>

<PageHeader {title} description={`Run ${shortId(id)}…`}>
	{#snippet actions()}
		{#if summary}
			<Badge tone="neutral">{summary.environment}</Badge>
		{/if}
		{#if summary && summary.workflowName !== '(deleted)'}
			<Button variant="secondary" href={`/workflows/${summary.workflowId}`}>
				<WorkflowIcon size={15} />Open workflow
			</Button>
		{/if}
		<Button variant="ghost" onclick={() => load(id, true)} loading={loading && Boolean(detail)}>
			<RefreshCw size={15} />Refresh
		</Button>
	{/snippet}
</PageHeader>

{#if error && !summary}
	<ErrorState {error} onRetry={() => load(id)} />
{:else}
	<div class="space-y-5">
		{#if summary}
			<RunSummaryCard run={summary} />
		{:else}
			<Card>
				<div class="grid grid-cols-2 gap-4 md:grid-cols-4">
					{#each { length: 8 } as _, i (i)}
						<div class="space-y-2">
							<Skeleton class="h-3 w-16" />
							<Skeleton class="h-4 w-24" />
						</div>
					{/each}
				</div>
			</Card>
		{/if}

		{#if error}
			<ErrorState {error} onRetry={() => load(id)} compact />
		{:else if !detail}
			<section>
				<h2 class="mb-3 text-sm font-semibold tracking-tight text-surface-800-200">Steps</h2>
				<div class="space-y-2">
					{#each { length: 3 } as _, i (i)}
						<Skeleton class="h-11 w-full rounded-lg" />
					{/each}
				</div>
			</section>
		{:else}
			{#if detail.steps.length > 0}
				<section>
					<h2 class="mb-3 text-sm font-semibold tracking-tight text-surface-800-200">Data flow</h2>
					<Card>
						<RunDataFlow steps={detail.steps} />
					</Card>
				</section>
			{/if}

			{#if hasInput || detail.targetDevices.length > 0}
				<section>
					<h2 class="mb-3 text-sm font-semibold tracking-tight text-surface-800-200">Input</h2>
					<Card>
						{#if detail.targetDevices.length > 0}
							<div class="text-xs text-surface-600-400">
								Targeted {detail.targetDevices.length}
								{detail.targetDevices.length === 1 ? 'device' : 'devices'}
							</div>
							<div class="mt-1.5 flex flex-wrap gap-1.5">
								{#each detail.targetDevices as device (device)}
									<a
										href={`/devices/${device}`}
										class="rounded bg-surface-200-800 px-1.5 py-0.5 font-mono text-[11px] text-surface-700-300 hover:underline"
										>{shortId(device)}</a
									>
								{/each}
							</div>
						{/if}
						{#if hasInput}
							<div class="text-xs text-surface-600-400" class:mt-3={detail.targetDevices.length > 0}>
								Feeds <code class="font-mono">{'{{ input.* }}'}</code> in every node.
							</div>
							<pre
								class="mt-1.5 max-h-48 overflow-auto rounded-lg bg-surface-100-900 p-2.5 font-mono text-[11px] leading-relaxed text-surface-800-200">{pretty(
									detail.input
								)}</pre>
						{/if}
					</Card>
				</section>
			{/if}

			<section>
				<div class="mb-3 flex flex-wrap items-center justify-between gap-2">
					<h2 class="text-sm font-semibold tracking-tight text-surface-800-200">Steps</h2>
					<div class="flex flex-wrap items-center gap-1.5 text-xs tabular-nums">
						{#if detail.changedCount > 0}<Badge tone="success">{detail.changedCount} changed</Badge>{/if}
						{#if detail.failedCount > 0}<Badge tone="error">{detail.failedCount} failed</Badge>{/if}
						{#if skipped > 0}<Badge tone="warning">{skipped} skipped</Badge>{/if}
						{#if noChange > 0}<Badge tone="neutral">{noChange} no change</Badge>{/if}
						<span class="text-surface-600-400">{detail.nodeCount} total</span>
					</div>
				</div>

				{#if nothingChanged}
					<p class="mb-3 rounded-lg bg-surface-100-900/60 px-3 py-2 text-xs text-surface-600-400">
						Nothing changed. Every node reported no change — open a step to see what it returned,
						or whether it returned anything at all.
					</p>
				{/if}

				{#if detail.steps.length === 0}
					<Card>
						<p class="py-6 text-center text-sm text-surface-600-400">No step records for this run.</p>
					</Card>
				{:else}
					<div class="space-y-2">
						{#each detail.steps as step (step.sequence)}
							<RunStepCard runId={detail.workflowRunId} {step} autoOpen={step.result === 'failed'} />
						{/each}
					</div>
					<p class="mt-2 text-xs text-surface-600-400">
						{detail.steps.length} step{detail.steps.length === 1 ? '' : 's'}
					</p>
				{/if}
			</section>
		{/if}
	</div>
{/if}
