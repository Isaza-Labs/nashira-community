<script lang="ts">
	// Groups the tool calls that accompany an assistant message into one
	// collapsible block. While a call is still running the block auto-expands so
	// progress is visible; once every call finishes it collapses to a summary row.
	// Tool names are humanized via TOOL_LABELS — unknown names fall back to the raw
	// snake_case (de-underscored) so a newly added tool still shows up.
	import { ChevronDown, Wrench, CheckCircle2, XCircle, Loader2 } from 'lucide-svelte';
	import type { ToolInvocation } from '$lib/stores/chat.svelte';
	import { toolLabel } from '$lib/utils/tool-labels';

	let { calls }: { calls: ToolInvocation[] } = $props();

	const total = $derived(calls.length);
	const running = $derived(calls.some((c) => c.status === 'running'));
	const failed = $derived(calls.some((c) => c.status === 'failed'));
	const allDone = $derived(!running && total > 0);

	// Open while something runs, collapsed once finished; a manual toggle sticks.
	let userOverride = $state<boolean | null>(null);
	const open = $derived(userOverride ?? running);
	function toggle() {
		userOverride = !open;
	}

	const summary = $derived.by(() => {
		if (running) {
			const last = [...calls].reverse().find((c) => c.status === 'running');
			return last ? `Running ${toolLabel(last.name)}…` : 'Working…';
		}
		if (failed) {
			const bad = calls.filter((c) => c.status === 'failed').length;
			return `Ran ${total} ${total === 1 ? 'tool' : 'tools'} · ${bad} failed`;
		}
		return `Ran ${total} ${total === 1 ? 'tool' : 'tools'}`;
	});
</script>

{#if total > 0}
	<div class="overflow-hidden rounded-lg border border-surface-200-800/80 bg-surface-50-900">
		<button
			type="button"
			onclick={toggle}
			class="flex w-full items-center gap-2 px-3 py-2 text-xs text-surface-700-300 transition-colors hover:bg-surface-200-800/30"
		>
			{#if running}
				<Loader2 size={12} class="shrink-0 animate-spin text-primary-700-300" />
			{:else if failed}
				<XCircle size={12} class="shrink-0 text-error-600-400" />
			{:else}
				<CheckCircle2 size={12} class="shrink-0 text-success-600-400" />
			{/if}
			<Wrench size={11} class="shrink-0 text-surface-600-400" />
			<span class="flex-1 truncate text-left">{summary}</span>
			{#if allDone}
				<span class="text-[10px] tabular-nums text-surface-600-400">{total}</span>
			{/if}
			<ChevronDown
				size={12}
				class="shrink-0 text-surface-600-400 transition-transform duration-150 {open ? 'rotate-180' : ''}"
			/>
		</button>

		{#if open}
			<ul class="divide-y divide-surface-200-800/50 border-t border-surface-200-800/80">
				{#each calls as tc, i (i)}
					<li class="flex items-center gap-2 px-3 py-1.5 text-xs">
						{#if tc.status === 'running'}
							<Loader2 size={11} class="shrink-0 animate-spin text-primary-700-300" />
						{:else if tc.status === 'ok'}
							<CheckCircle2 size={11} class="shrink-0 text-success-600-400" />
						{:else}
							<XCircle size={11} class="shrink-0 text-error-600-400" />
						{/if}
						<span class="flex-1 truncate text-surface-800-200">{toolLabel(tc.name)}</span>
						<code
							class="max-w-[240px] truncate font-mono text-[10px] text-surface-600-400"
							title={tc.name}
						>
							{tc.name}
						</code>
					</li>
				{/each}
			</ul>
		{/if}
	</div>
{/if}
