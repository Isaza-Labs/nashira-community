<script lang="ts">
	// Every run, across every workflow.
	//
	// The per-workflow tab answers "what has this one done". This answers "what happened
	// last night", which is the question the day actually starts with and the one the
	// dashboard's top-failing list pointed at without being able to open.
	//
	// Filtering is done by the server, so the count beside the pager describes the same
	// set as the rows. Narrowing the page in the browser is cheaper to write and gives a
	// list that says "3 results" next to a pager built from 412 — and the reader
	// believes the smaller number.
	import { untrack } from 'svelte';
	import { listFleetRuns, type FleetRun } from '$lib/api/fleet.api';
	import { prefetchFleetRun, rememberFleetRuns } from '$lib/api/runs.api';
	import {
		PageHeader,
		Card,
		Select,
		SearchInput,
		Badge,
		Pagination,
		Spinner,
		ErrorState,
		EmptyState,
		Button,
		toast
	} from '$lib/components/ui';
	import { RefreshCw, ChevronRight } from 'lucide-svelte';

	let rows = $state<FleetRun[]>([]);
	let total = $state(0);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let status = $state('');
	let environment = $state('');
	let finalState = $state('');
	let trigger = $state('');
	let q = $state('');
	let offset = $state(0);
	const limit = 50;

	let seq = 0;

	async function load() {
		const mine = ++seq;
		error = null;
		try {
			const page = await listFleetRuns({
				status: status || undefined,
				environment: environment || undefined,
				finalState: finalState || undefined,
				q: q.trim() || undefined,
				limit,
				offset
			});
			if (mine !== seq) return;
			rows = page.items;
			total = page.total;
			// The detail page paints its header from these before its own fetch lands.
			rememberFleetRuns(rows);
		} catch (e) {
			if (mine !== seq) return;
			if (rows.length === 0) error = e;
			else toast.fromError(e, "Couldn't refresh the runs");
		} finally {
			if (mine === seq) loading = false;
		}
	}

	// A filter change must return to the first page: page 6 of the old set is very
	// likely past the end of the new one, and an empty list reads as "no results".
	$effect(() => {
		status;
		environment;
		finalState;
		trigger;
		q;
		untrack(() => {
			if (offset !== 0) offset = 0;
			else load();
		});
	});

	$effect(() => {
		offset;
		untrack(() => load());
	});

	function statusTone(s: string): 'success' | 'error' | 'primary' | 'neutral' {
		if (s === 'completed') return 'success';
		if (s === 'failed') return 'error';
		if (s === 'running') return 'primary';
		return 'neutral';
	}

	// The distinction the engine records and the status alone does not carry: a failed
	// run that rolled everything back is contained; one that stopped halfway is not.
	function outcome(r: FleetRun): { label: string; tone: 'error' | 'warning' | 'neutral' } | null {
		if (r.status !== 'failed') return null;
		if (r.finalState === 'rolled_back')
			return { label: 'rolled back', tone: 'warning' };
		if (r.finalState === 'failed')
			return { label: 'left changes behind', tone: 'error' };
		return null;
	}

	function duration(seconds: number | null): string {
		if (seconds === null) return 'running';
		if (seconds < 60) return `${seconds}s`;
		if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
		return `${(seconds / 3600).toFixed(1)}h`;
	}

	function when(at: string): string {
		return new Date(at).toLocaleString(undefined, {
			day: '2-digit',
			month: 'short',
			hour: '2-digit',
			minute: '2-digit',
			hour12: false
		});
	}

	// Filtered in the browser, unlike the rest: the source is one short enum and the
	// server does not index it, so a round trip would buy nothing. The count beside the
	// pager still describes the server's set — see the note at the top of this file —
	// so this filter deliberately does not claim to.
	const visible = $derived(trigger ? rows.filter((r) => r.trigger === trigger) : rows);

	const TRIGGER_LABELS: Record<string, string> = {
		manual: 'by hand',
		agent: 'the agent',
		schedule: 'a schedule',
		webhook: 'a webhook',
		git_webhook: 'a git push',
		test: 'a test',
		subflow: 'a parent run'
	};

	function triggerLabel(t: string): string {
		return TRIGGER_LABELS[t] ?? t;
	}

	const filtered = $derived(Boolean(status || environment || finalState || trigger || q.trim()));
</script>

<svelte:head><title>Runs · Nashira</title></svelte:head>

<PageHeader title="Runs" description="Every execution, across every workflow.">
	{#snippet actions()}
		<Button variant="ghost" onclick={load}><RefreshCw size={15} />Refresh</Button>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<div class="space-y-4">
		<Card>
			<div class="flex flex-wrap items-end gap-2">
				<div class="w-40">
					<Select
						bind:value={status}
						label="Status"
						options={[
							{ value: '', label: 'Any status' },
							{ value: 'running', label: 'Running' },
							{ value: 'completed', label: 'Completed' },
							{ value: 'failed', label: 'Failed' }
						]}
					/>
				</div>
				<div class="w-40">
					<Select
						bind:value={environment}
						label="Environment"
						options={[
							{ value: '', label: 'Any environment' },
							{ value: 'draft', label: 'Draft' },
							{ value: 'qa', label: 'QA' },
							{ value: 'production', label: 'Production' }
						]}
					/>
				</div>
				<div class="w-52">
					<Select
						bind:value={finalState}
						label="Outcome"
						options={[
							{ value: '', label: 'Any outcome' },
							{ value: 'completed', label: 'Completed' },
							{ value: 'rolled_back', label: 'Rolled back' },
							{ value: 'failed', label: 'Left changes behind' }
						]}
					/>
				</div>
				<div class="w-44">
					<Select
						bind:value={trigger}
						label="Started by"
						options={[
							{ value: '', label: 'Anything' },
							{ value: 'manual', label: 'By hand' },
							{ value: 'agent', label: 'The agent' },
							{ value: 'schedule', label: 'A schedule' },
							{ value: 'webhook', label: 'A webhook' },
							{ value: 'git_webhook', label: 'A git push' },
							{ value: 'test', label: 'A test' }
						]}
					/>
				</div>
				<div class="min-w-56 flex-1">
					<SearchInput bind:value={q} width="w-full" placeholder="Workflow name…" />
				</div>
			</div>
		</Card>

		{#if loading && rows.length === 0}
			<div class="flex justify-center py-12"><Spinner size="lg" /></div>
		{:else if visible.length === 0}
			<EmptyState
				title={filtered ? 'Nothing matches' : 'Nothing has run yet'}
				description={filtered
					? 'No run matches these filters. Clear one to widen the search.'
					: 'Runs appear here as soon as a workflow is executed or a trigger fires.'}
			/>
		{:else}
			<Card>
				<div class="overflow-x-auto">
					<table class="w-full text-sm">
						<thead class="text-left text-xs uppercase tracking-wide text-surface-600-400">
							<tr class="border-b border-surface-200-800">
								<th class="py-2 pr-3 font-medium">Started</th>
								<th class="py-2 pr-3 font-medium">Workflow</th>
								<th class="py-2 pr-3 font-medium">Env</th>
								<th class="py-2 pr-3 font-medium">Started by</th>
								<th class="py-2 pr-3 font-medium">Status</th>
								<th class="py-2 pr-3 text-right font-medium">Took</th>
								<th class="py-2 pr-3 text-right font-medium">Changed</th>
								<th class="py-2 text-right font-medium"><span class="sr-only">Open</span></th>
							</tr>
						</thead>
						<tbody>
							{#each visible as r (r.workflowRunId)}
								{@const o = outcome(r)}
								<tr class="border-b border-surface-100-900 hover:bg-surface-100-900/60">
									<!-- Hovering warms the detail so the click that follows finds it
									     already here. -->
									<td class="py-2 pr-3 whitespace-nowrap tabular-nums text-xs">
										<a
											href={`/runs/${r.workflowRunId}`}
											class="font-medium text-primary-700-300 hover:underline"
											onmouseenter={() => prefetchFleetRun(r.workflowRunId)}
											onfocus={() => prefetchFleetRun(r.workflowRunId)}
										>
											{when(r.startedAt)}
										</a>
									</td>
									<td class="py-2 pr-3">
										<a class="hover:underline" href={`/workflows/${r.workflowId}`}>
											{r.workflowName}
										</a>
									</td>
									<td class="py-2 pr-3 text-xs text-surface-600-400">{r.environment}</td>
									<td class="py-2 pr-3 text-xs text-surface-600-400">{triggerLabel(r.trigger)}</td>
									<td class="py-2 pr-3">
										<div class="flex flex-wrap items-center gap-1.5">
											<Badge tone={statusTone(r.status)}>{r.status}</Badge>
											{#if o}<Badge tone={o.tone}>{o.label}</Badge>{/if}
											<!-- A refusal shows as failed with 0 of 0 nodes, which reads
											     like a platform bug rather than a run that was stopped
											     on purpose. -->
											{#if r.error && r.nodeCount === 0}
												<Badge tone="warning">refused</Badge>
											{/if}
										</div>
										{#if r.error}
											<p class="mt-0.5 max-w-md truncate text-[11px] text-error-700-300" title={r.error}>
												{r.error}
											</p>
										{/if}
									</td>
									<td
										class="py-2 pr-3 text-right tabular-nums text-xs"
										class:text-primary-700-300={r.durationSeconds === null}
									>
										{duration(r.durationSeconds)}
									</td>
									<td class="py-2 pr-3 text-right tabular-nums text-xs text-surface-600-400">
										{r.changedCount}/{r.nodeCount}
									</td>
									<td class="py-2 text-right">
										<a
											href={`/runs/${r.workflowRunId}`}
											class="inline-flex items-center gap-0.5 whitespace-nowrap text-xs text-surface-600-400 hover:text-surface-950-50"
											onmouseenter={() => prefetchFleetRun(r.workflowRunId)}
										>
											Details<ChevronRight size={13} />
										</a>
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>

				<div class="mt-3 border-t border-surface-200-800 pt-3">
					<Pagination {total} {limit} {offset} onchange={(o: number) => (offset = o)} />
				</div>
			</Card>
		{/if}
	</div>
{/if}
