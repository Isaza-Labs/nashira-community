<script lang="ts">
	// One workflow run, in enough detail to answer "what did it actually do".
	//
	// This used to be a status badge, a node id and a result word per step. On a
	// read-only workflow that reads as nothing at all: every node reports
	// `no_change`, which is the same word a node that pinged a host and found it
	// healthy produces, and the same word a node that never executed produces. The
	// engine was already storing each node's output and the API was already
	// carrying the run's input and targets — none of it reached the screen.
	//
	// So: counts that include the outcomes the summary line used to omit, the
	// input the run was given, and per-step output on demand. Output is collapsed
	// by default because a per-device node returns a payload per device, and a
	// twelve-device run would otherwise bury the shape of the run itself.
	import { StatusBadge, Badge } from '$lib/components/ui';
	import type { WorkflowRunDetail, StepRun } from '$lib/api/workflows.api';
	import { ChevronRight } from 'lucide-svelte';

	let { run }: { run: WorkflowRunDetail } = $props();

	// Open steps, by sequence. A failed step opens itself — the reason a run
	// failed is the one thing nobody should have to click for.
	let open = $state<Record<number, boolean>>({});
	$effect(() => {
		const initial: Record<number, boolean> = {};
		for (const s of run.steps) if (s.result === 'failed') initial[s.sequence] = true;
		open = initial;
	});

	function toggle(seq: number) {
		open = { ...open, [seq]: !open[seq] };
	}

	// A person reads "webhook"; the column stores git_webhook.
	const triggerLabel = $derived(
		{
			manual: 'started by hand',
			agent: 'started by the agent',
			schedule: 'fired by a schedule',
			webhook: 'started by a webhook',
			git_webhook: 'started by a git push',
			test: 'an acceptance test',
			subflow: 'started by a parent run'
		}[run.trigger] ?? run.trigger
	);

	// Counted here rather than read off the run: the run row carries changed and
	// failed only, and a step that was skipped is exactly what this panel exists
	// to make visible.
	const skipped = $derived(run.steps.filter((s) => s.result === 'skipped').length);
	const noChange = $derived(run.steps.filter((s) => s.result === 'no_change').length);

	const duration = $derived.by(() => {
		if (!run.finishedAt) return null;
		const ms = new Date(run.finishedAt).getTime() - new Date(run.startedAt).getTime();
		if (!Number.isFinite(ms) || ms < 0) return null;
		if (ms < 1000) return `${ms}ms`;
		if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
		return `${Math.floor(ms / 60_000)}m ${Math.round((ms % 60_000) / 1000)}s`;
	});

	// Cap on what one block will put in the DOM. A per-device node can return
	// megabytes, and a failed step opens itself — without the cap, opening a run
	// meant laying out the entire payload before anything appeared.
	const RENDER_LIMIT = 200_000;

	function clip(text: string): string {
		if (text.length <= RENDER_LIMIT) return text;
		return (
			text.slice(0, RENDER_LIMIT) +
			`\n… truncated for display — ${text.length.toLocaleString()} characters total`
		);
	}

	function pretty(value: unknown): string {
		try {
			return clip(JSON.stringify(value, null, 2));
		} catch {
			return clip(String(value));
		}
	}

	// The message that says what went wrong, on its own line because `error_code`
	// names a category and the sentence is what a reader acts on. Prefers the step's
	// own field; falls back to `output.error` for runs recorded before that column
	// existed, so old runs stay as readable as they were.
	function errorMessage(s: StepRun): string | null {
		if (s.error && s.error.trim()) return s.error;
		const o = s.output;
		if (o && typeof o === 'object' && !Array.isArray(o)) {
			const e = (o as Record<string, unknown>).error;
			if (typeof e === 'string' && e.trim()) return e;
		}
		return null;
	}

	function hasOutput(s: StepRun): boolean {
		return s.output !== null && s.output !== undefined;
	}

	function hasInputSnapshot(s: StepRun): boolean {
		return s.input !== null && s.input !== undefined;
	}

	// Expandable when there is anything underneath — output, the resolved input, or
	// the handler's own account. A step with only logs used to look like a dead end.
	function expandable(s: StepRun): boolean {
		return hasOutput(s) || hasInputSnapshot(s) || Boolean(s.logs);
	}

	function stepDuration(s: StepRun): string | null {
		if (s.durationMs === null || s.durationMs === undefined) return null;
		if (s.durationMs < 1000) return `${s.durationMs}ms`;
		if (s.durationMs < 60_000) return `${(s.durationMs / 1000).toFixed(1)}s`;
		return `${Math.floor(s.durationMs / 60_000)}m ${Math.round((s.durationMs % 60_000) / 1000)}s`;
	}

	// Only worth showing when it is not the ordinary single attempt.
	//
	// "never ran" is gated on the step having failed, and that guard is the whole
	// point: attempts is 0 for exactly one real case — a node whose snippet never
	// resolved, which always fails — and 0 is also what every row written before the
	// column existed carries. Without the guard the panel printed "never ran" beside a
	// step's own output, contradicting itself on every historical run. A result word
	// and a badge that disagree make the reader distrust both, so the badge defers.
	function attemptNote(s: StepRun): string | null {
		if (s.attempts === 0) return s.result === 'failed' ? 'never ran' : null;
		if (s.attempts > 1) return `${s.attempts} attempts`;
		return null;
	}

	const hasInput = $derived(
		run.input !== null &&
			run.input !== undefined &&
			!(
				typeof run.input === 'object' &&
				run.input !== null &&
				Object.keys(run.input).length === 0
			)
	);
</script>

<div class="overflow-hidden rounded-xl border border-surface-200-800">
	<div class="flex flex-wrap items-center gap-3 border-b border-surface-200-800 px-4 py-2.5">
		<StatusBadge status={run.status} />
		<span class="text-sm text-surface-700-300">
			Final state: <span class="font-medium text-surface-900-100">{run.finalState}</span>
		</span>
		{#if duration}
			<span class="text-xs tabular-nums text-surface-600-400">took {duration}</span>
		{/if}
		<span class="text-xs text-surface-600-400">· {triggerLabel}</span>
		<span class="ml-auto inline-flex flex-wrap items-center gap-1.5 text-xs tabular-nums">
			{#if run.changedCount > 0}<Badge tone="success">{run.changedCount} changed</Badge>{/if}
			{#if run.failedCount > 0}<Badge tone="error">{run.failedCount} failed</Badge>{/if}
			{#if skipped > 0}<Badge tone="warning">{skipped} skipped</Badge>{/if}
			{#if noChange > 0}<Badge tone="neutral">{noChange} no change</Badge>{/if}
			<span class="text-surface-600-400">{run.nodeCount} total</span>
		</span>
	</div>

	<!-- A run refused before it began has no steps to explain it, so this is the only
	     place the reason can appear. Without it the screen said "failed" and showed an
	     empty step list. -->
	{#if run.error}
		<div class="border-b border-surface-200-800 bg-error-50-950/40 px-4 py-2.5">
			<div class="text-xs font-medium text-error-700-300">
				{run.nodeCount === 0 ? 'Nothing ran' : 'The run itself failed'}
			</div>
			<p class="mt-0.5 text-xs text-error-700-300">{run.error}</p>
		</div>
	{/if}

	{#if run.changedCount === 0 && run.failedCount === 0 && run.nodeCount > 0}
		<p
			class="border-b border-surface-200-800 bg-surface-100-900/40 px-4 py-2 text-xs text-surface-600-400"
		>
			Nothing changed. Every node reported no change — expand a step to see what it returned,
			or whether it returned anything at all.
		</p>
	{/if}

	{#if hasInput || run.targetDevices.length > 0}
		<div class="border-b border-surface-200-800 px-4 py-2.5 text-xs">
			{#if run.targetDevices.length > 0}
				<div class="text-surface-600-400">
					Targeted {run.targetDevices.length}
					{run.targetDevices.length === 1 ? 'device' : 'devices'}
				</div>
			{/if}
			{#if hasInput}
				<details class="mt-1">
					<summary class="cursor-pointer text-surface-600-400 hover:text-surface-900-100">
						Input
					</summary>
					<pre
						class="mt-1.5 max-h-48 overflow-auto rounded-lg bg-surface-100-900 p-2.5 font-mono text-[11px] leading-relaxed text-surface-800-200">{pretty(
							run.input
						)}</pre>
				</details>
			{/if}
		</div>
	{/if}

	{#if run.steps.length > 0}
		<ol class="divide-y divide-surface-100-900">
			{#each run.steps as s (s.sequence)}
				{@const message = errorMessage(s)}
				{@const canExpand = expandable(s)}
				{@const took = stepDuration(s)}
				{@const attempts = attemptNote(s)}
				<li>
					<div class="flex items-center gap-3 px-4 py-2 text-sm">
						<span class="w-5 shrink-0 text-xs tabular-nums text-surface-600-400">
							{s.sequence}
						</span>
						{#if canExpand}
							<button
								type="button"
								class="flex min-w-0 flex-1 items-center gap-1.5 text-left hover:text-surface-900-100"
								aria-expanded={Boolean(open[s.sequence])}
								onclick={() => toggle(s.sequence)}
							>
								<ChevronRight
									size={13}
									class="shrink-0 text-surface-600-400 transition-transform {open[s.sequence]
										? 'rotate-90'
										: ''}"
								/>
								<code class="min-w-0 truncate text-xs text-surface-700-300">{s.nodeId}</code>
							</button>
						{:else}
							<code class="min-w-0 flex-1 truncate pl-[19px] text-xs text-surface-700-300">
								{s.nodeId}
							</code>
						{/if}
						{#if s.errorCode}
							<span class="truncate text-xs text-error-500" title={s.errorCode}>
								{s.errorCode}
							</span>
						{/if}
						{#if attempts}
							<span class="shrink-0 text-[11px] text-warning-700-300">{attempts}</span>
						{/if}
						{#if took}
							<span class="shrink-0 text-[11px] tabular-nums text-surface-600-400">{took}</span>
						{/if}
						{#if !canExpand && s.result !== 'skipped'}
							<span class="shrink-0 text-[11px] text-surface-600-400">no output</span>
						{/if}
						<StatusBadge status={s.result} />
					</div>

					{#if message}
						<p class="px-4 pb-2 pl-[52px] text-xs text-error-700-300">{message}</p>
					{/if}

					{#if canExpand && open[s.sequence]}
						<div class="mx-4 mb-2.5 ml-[52px] space-y-2">
							{#if s.logs}
								<!-- What the handler says it did. On a step that changed nothing
								     this is the only thing that distinguishes "checked and found
								     nothing to do" from "did not really run". -->
								<pre
									class="max-h-40 overflow-auto rounded-lg bg-surface-100-900 p-2.5 font-mono text-[11px] leading-relaxed whitespace-pre-wrap text-surface-800-200">{clip(s.logs)}</pre>
							{/if}

							{#if hasInputSnapshot(s)}
								<details>
									<summary
										class="cursor-pointer text-[11px] text-surface-600-400 hover:text-surface-900-100"
									>
										Input as resolved
									</summary>
									<pre
										class="mt-1.5 max-h-56 overflow-auto rounded-lg bg-surface-100-900 p-2.5 font-mono text-[11px] leading-relaxed text-surface-800-200">{pretty(
											s.input
										)}</pre>
									<p class="mt-1 text-[11px] text-surface-600-400">
										Templates already substituted; values that look like secrets are redacted.
									</p>
								</details>
							{/if}

							{#if hasOutput(s)}
								<pre
									class="max-h-72 overflow-auto rounded-lg bg-surface-100-900 p-2.5 font-mono text-[11px] leading-relaxed text-surface-800-200">{pretty(
										s.output
									)}</pre>
							{/if}
						</div>
					{/if}
				</li>
			{/each}
		</ol>
	{:else}
		<p class="px-4 py-3 text-xs text-surface-600-400">No step records for this run.</p>
	{/if}
</div>
