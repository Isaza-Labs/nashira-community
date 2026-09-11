<script lang="ts">
	// One step of a run, as a card: header with the verdict, then Output / Input /
	// Logs / Error tabs — the same shape as Flow Weaver's step detail.
	//
	// The payloads are not on the step the page received; they are fetched the first
	// time the card is opened. A failed step opens itself, because the reason a run
	// failed is the one thing nobody should have to click for — and it opens on the
	// Error tab, where that reason is.
	import { untrack } from 'svelte';
	import {
		getFleetRunStep,
		prefetchFleetRun,
		type FleetStep,
		type FleetStepPayload
	} from '$lib/api/runs.api';
	import { StatusBadge, Spinner, ErrorState } from '$lib/components/ui';
	import { ChevronRight } from 'lucide-svelte';
	import { clip, payloadSize, pretty, shortId, stepDuration } from './format';

	let {
		runId,
		step,
		autoOpen = false
	}: {
		runId: string;
		step: FleetStep;
		/** Open on mount — used for failed steps. */
		autoOpen?: boolean;
	} = $props();

	type Tab = 'output' | 'input' | 'logs' | 'error';

	let open = $state(false);
	let activeTab = $state<Tab | null>(null);
	let payload = $state<FleetStepPayload | null>(null);
	let loading = $state(false);
	let error = $state<unknown>(null);

	const hasPayload = $derived(step.outputChars > 0 || step.inputChars > 0 || step.logsChars > 0);
	const hasError = $derived(Boolean(step.error && step.error.trim()));
	// Expandable when there is anything underneath. A step with only an error still
	// gets a card body, so the message has room to be read in full.
	const expandable = $derived(hasPayload || hasError);

	const tabs = $derived.by<{ id: Tab; label: string; size: string | null }[]>(() => {
		const list: { id: Tab; label: string; size: string | null }[] = [];
		if (step.outputChars > 0) list.push({ id: 'output', label: 'Output', size: payloadSize(step.outputChars) });
		if (step.inputChars > 0) list.push({ id: 'input', label: 'Input', size: payloadSize(step.inputChars) });
		if (step.logsChars > 0) list.push({ id: 'logs', label: 'Logs', size: payloadSize(step.logsChars) });
		if (hasError) list.push({ id: 'error', label: 'Error', size: null });
		return list;
	});

	const took = $derived(stepDuration(step.durationMs));

	// Only worth showing when it is not the ordinary single attempt. "never ran" is
	// gated on the step having failed: attempts is 0 for a node whose snippet never
	// resolved, and also for every row written before the column existed.
	const attemptNote = $derived.by(() => {
		if (step.attempts === 0) return step.result === 'failed' ? 'never ran' : null;
		if (step.attempts > 1) return `${step.attempts} attempts`;
		return null;
	});

	async function load() {
		if (payload || loading || !hasPayload) return;
		loading = true;
		error = null;
		try {
			payload = await getFleetRunStep(runId, step.sequence);
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	function toggle() {
		if (!expandable) return;
		open = !open;
		if (open) {
			if (!activeTab) activeTab = hasError ? 'error' : (tabs[0]?.id ?? null);
			void load();
		}
	}

	$effect(() => {
		if (autoOpen && expandable) untrack(() => { if (!open) toggle(); });
	});
</script>

<div class="overflow-hidden rounded-lg border border-surface-200-800 bg-surface-100-900">
	<button
		type="button"
		class="flex w-full items-center gap-3 px-4 py-2.5 text-left {expandable
			? 'hover:bg-surface-200-800/40'
			: 'cursor-default'}"
		aria-expanded={expandable ? open : undefined}
		onclick={toggle}
	>
		<ChevronRight
			size={14}
			class="shrink-0 text-surface-600-400 transition-transform {open ? 'rotate-90' : ''} {expandable
				? ''
				: 'invisible'}"
		/>
		<span class="w-5 shrink-0 text-xs tabular-nums text-surface-600-400">{step.sequence}</span>
		<StatusBadge status={step.result} />
		<span class="min-w-0 truncate font-mono text-sm text-surface-900-100">{step.nodeId}</span>
		{#if step.errorCode}
			<span class="truncate text-xs text-error-700-300" title={step.errorCode}>{step.errorCode}</span>
		{/if}
		<span class="ml-auto flex shrink-0 items-center gap-4 text-xs text-surface-600-400">
			{#if attemptNote}<span class="text-warning-700-300">{attemptNote}</span>{/if}
			{#if took}
				<span>Duration: <span class="font-mono tabular-nums text-surface-700-300">{took}</span></span>
			{/if}
			{#if !hasPayload && step.result !== 'skipped'}<span>no output</span>{/if}
		</span>
	</button>

	{#if hasError && !open}
		<p class="px-4 pb-2.5 pl-[68px] text-xs text-error-700-300">{step.error}</p>
	{/if}

	<!-- A subflow step's detail is not a payload, so no tab can hold it: it is another
	     run, with its own steps and its own page. Outside the header button because a
	     link inside a button is neither. -->
	{#if step.childRunId}
		<p class="px-4 pb-2.5 pl-[68px] text-xs">
			<a
				href={`/runs/${step.childRunId}`}
				class="inline-flex items-center gap-0.5 text-primary-700-300 hover:underline"
				onmouseenter={() => prefetchFleetRun(step.childRunId!)}
				onfocus={() => prefetchFleetRun(step.childRunId!)}
			>
				Open the child run {shortId(step.childRunId)}…
				<ChevronRight size={12} />
			</a>
		</p>
	{/if}

	{#if open}
		<div class="flex border-t border-b border-surface-200-800">
			{#each tabs as tab (tab.id)}
				{@const isActive = activeTab === tab.id}
				<button
					type="button"
					onclick={() => (activeTab = tab.id)}
					class="relative px-4 py-2 text-xs font-medium transition-colors {isActive
						? 'text-primary-700-300'
						: tab.id === 'error'
							? 'text-error-700-300'
							: 'text-surface-600-400 hover:text-surface-800-200'}"
				>
					{tab.label}
					{#if tab.size}<span class="ml-1 font-normal text-surface-600-400">{tab.size}</span>{/if}
					{#if tab.id === 'error'}
						<span class="ml-1 inline-block h-1.5 w-1.5 rounded-full bg-error-500"></span>
					{/if}
					{#if isActive}
						<span class="absolute inset-x-0 -bottom-px h-px bg-primary-500 dark:bg-primary-400"></span>
					{/if}
				</button>
			{/each}
		</div>

		<div class="max-h-80 overflow-auto bg-surface-50-950 p-3">
			{#if activeTab === 'error'}
				<pre class="font-mono text-xs whitespace-pre-wrap text-error-700-300">{step.error}</pre>
			{:else if error}
				<ErrorState {error} onRetry={load} compact />
			{:else if loading || !payload}
				<div class="flex justify-center py-4"><Spinner /></div>
			{:else if activeTab === 'output'}
				<pre class="font-mono text-xs whitespace-pre-wrap text-success-700-300">{payload.output === null
						? '(no output)'
						: pretty(payload.output)}</pre>
			{:else if activeTab === 'input'}
				<pre class="font-mono text-xs whitespace-pre-wrap text-warning-700-300">{payload.input === null
						? '(no input)'
						: pretty(payload.input)}</pre>
				<p class="mt-2 text-[11px] text-surface-600-400">
					Templates already substituted; values that look like secrets are redacted.
				</p>
			{:else if activeTab === 'logs'}
				<pre class="font-mono text-xs whitespace-pre-wrap text-surface-700-300">{payload.logs
						? clip(payload.logs)
						: '(no logs)'}</pre>
			{/if}
		</div>
	{/if}
</div>
