<script lang="ts">
	import type { Column, Density } from './types';

	// Placeholder rows shaped like the real table. A spinner says "wait"; this says
	// "the table is already there, the data is a moment out" — which is both truer
	// and calmer. Widths vary per column so it reads as content, not as a bar chart.
	let {
		columns,
		rows = 6,
		density = 'comfortable'
	}: { columns: Column[]; rows?: number; density?: Density } = $props();

	const pad = $derived(density === 'compact' ? 'px-3 py-1' : 'px-3 py-2');

	// Deterministic pseudo-widths: stable across renders (no layout jitter) but
	// uneven enough to look like text.
	const WIDTHS = ['w-24', 'w-32', 'w-20', 'w-28', 'w-16', 'w-36'];
	function width(rowIndex: number, colIndex: number): string {
		return WIDTHS[(rowIndex * 3 + colIndex * 5) % WIDTHS.length];
	}
</script>

{#each { length: rows } as _, r (r)}
	<tr class="border-b border-surface-100-900 last:border-0" aria-hidden="true">
		{#each columns as c, i (c.key)}
			<td class={pad}>
				<div
					class="h-4 max-w-full animate-pulse rounded bg-surface-200-800 {width(r, i)}"
					style={`animation-delay: ${r * 60}ms`}
				></div>
			</td>
		{/each}
	</tr>
{/each}
