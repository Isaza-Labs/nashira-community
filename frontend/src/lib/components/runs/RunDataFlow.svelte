<script lang="ts">
	// The run as a chain: one chip per step, in execution order, coloured and glyphed
	// by how it ended. Reads at a glance where a run stopped, which the step list
	// below only says one row at a time.
	//
	// State is conveyed by colour + glyph + label so it does not rely on colour alone.
	import type { FleetStep } from '$lib/api/runs.api';
	import { ArrowRight, CheckCircle2, XCircle, MinusCircle, Circle } from 'lucide-svelte';
	import { stepTone } from './format';

	let { steps }: { steps: FleetStep[] } = $props();

	function glyph(result: string) {
		switch (result) {
			case 'changed':
				return CheckCircle2;
			case 'failed':
				return XCircle;
			case 'skipped':
				return MinusCircle;
			default:
				return Circle;
		}
	}

	const chip: Record<string, string> = {
		success: 'border-success-500/40 bg-success-500/10 text-success-700-300',
		error: 'border-error-500/40 bg-error-500/10 text-error-700-300',
		warning: 'border-warning-500/40 bg-warning-500/10 text-warning-700-300',
		neutral: 'border-surface-300-700 bg-surface-200-800/40 text-surface-600-400',
		primary: 'border-primary-500/40 bg-primary-500/10 text-primary-700-300'
	};
	const dot: Record<string, string> = {
		success: 'bg-success-500',
		error: 'bg-error-500',
		warning: 'bg-warning-500',
		neutral: 'bg-surface-500',
		primary: 'bg-primary-500'
	};

	const legend = [
		{ result: 'changed', label: 'Changed' },
		{ result: 'no_change', label: 'No change' },
		{ result: 'failed', label: 'Failed' },
		{ result: 'skipped', label: 'Skipped' }
	];
</script>

<div class="mb-3 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-[11px] text-surface-600-400">
	{#each legend as item (item.result)}
		{@const Glyph = glyph(item.result)}
		<span class="inline-flex items-center gap-1.5">
			<span class={`h-2 w-2 rounded-full ${dot[stepTone(item.result)]}`}></span>
			<Glyph size={12} class="text-surface-600-400" />
			{item.label}
		</span>
	{/each}
</div>
<div class="overflow-x-auto">
	<div class="flex min-w-max items-center gap-2 py-2">
		{#each steps as step, i (step.sequence)}
			{@const tone = stepTone(step.result)}
			{@const Glyph = glyph(step.result)}
			<div class="flex flex-col items-center gap-1">
				<div
					class={`inline-flex min-w-[110px] items-center justify-center gap-1.5 rounded-md border px-3 py-2 text-center font-mono text-xs ${chip[tone]}`}
				>
					<Glyph size={12} class="shrink-0" />
					{step.nodeId}
				</div>
				<div class="text-[10px] text-surface-600-400">{step.result}</div>
			</div>
			{#if i < steps.length - 1}
				<ArrowRight
					size={14}
					class={step.result === 'failed' ? 'text-error-500' : 'text-success-500'}
				/>
			{/if}
		{/each}
	</div>
</div>
