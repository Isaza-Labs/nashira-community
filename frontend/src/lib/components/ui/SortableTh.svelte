<script lang="ts">
	import { ArrowUp, ArrowDown, ArrowUpDown } from 'lucide-svelte';
	import type { Snippet } from 'svelte';

	// Sortable table header cell. Drop inside a <thead><tr>; the parent owns the
	// sort state and re-sorts its rows in the onSort callback.
	let {
		column,
		sortKey,
		sortDir = 'asc',
		onSort,
		align = 'left',
		children
	}: {
		column: string;
		sortKey: string | null;
		sortDir?: 'asc' | 'desc';
		onSort: (column: string) => void;
		align?: 'left' | 'right' | 'center';
		children?: Snippet;
	} = $props();

	const active = $derived(sortKey === column);
	const alignClass = $derived(
		align === 'right' ? 'text-right' : align === 'center' ? 'text-center' : 'text-left'
	);
</script>

<th
	class="px-3 py-2 font-medium {alignClass}"
	aria-sort={active ? (sortDir === 'asc' ? 'ascending' : 'descending') : 'none'}
>
	<button
		type="button"
		class="inline-flex cursor-pointer items-center gap-1 hover:opacity-80 {align === 'right'
			? 'flex-row-reverse'
			: ''}"
		onclick={() => onSort(column)}
	>
		{#if children}{@render children()}{/if}
		{#if active}
			{#if sortDir === 'asc'}<ArrowUp size={11} />{:else}<ArrowDown size={11} />{/if}
		{:else}
			<ArrowUpDown size={11} class="opacity-40" />
		{/if}
	</button>
</th>
