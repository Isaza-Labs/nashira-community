<script lang="ts">
	// One numeric style knob in the theme editor: label, slider, live readout and
	// a reset back to the shipped value.
	//
	// The readout is not decoration. These two settings (corner roundness,
	// interface scale) change the whole app at once, and a slider with no number
	// next to it gives nobody a way to say "put it back to 1.15" — or to notice
	// that they are already sitting on the default and therefore overriding
	// nothing at all.
	import { RotateCcw } from 'lucide-svelte';
	import { IconButton } from '$lib/components/ui';

	let {
		label,
		hint,
		min,
		max,
		step,
		fallback,
		format = (v: number) => String(v),
		value = $bindable()
	}: {
		label: string;
		hint: string;
		min: number;
		max: number;
		step: number;
		/** The app default this returns to — and the value that means "inherit". */
		fallback: number;
		format?: (v: number) => string;
		value: number;
	} = $props();

	const uid = $props.id();
	const isDefault = $derived(value === fallback);
</script>

<div class="flex flex-wrap items-center gap-3 py-2">
	<div class="min-w-40 flex-1">
		<label for={uid} class="text-sm font-medium">{label}</label>
		<p class="text-xs text-surface-600-400">{hint}</p>
	</div>
	<input
		id={uid}
		type="range"
		{min}
		{max}
		{step}
		bind:value
		class="w-40 shrink-0 cursor-pointer accent-primary-500"
	/>
	<span class="w-14 shrink-0 text-right font-mono text-xs tabular-nums text-surface-700-300">
		{format(value)}
	</span>
	<IconButton
		label={`Reset ${label} to the stock value`}
		disabled={isDefault}
		onclick={() => (value = fallback)}
	>
		<RotateCcw size={14} />
	</IconButton>
</div>
