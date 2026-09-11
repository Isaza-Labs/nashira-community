<script lang="ts" generics="T extends Record<string, unknown>">
	import type { Snippet } from 'svelte';
	import type { Column, Density } from './types';
	import { ArrowDown, ArrowUp, ArrowUpDown } from 'lucide-svelte';
	import EmptyState from './EmptyState.svelte';
	import TableSkeleton from './TableSkeleton.svelte';

	// Generic table. Provide `columns` + `rows`; for custom cells pass a `cell`
	// snippet that switches on the column key, otherwise the raw value is shown.
	//
	// Sorting is client-side over the rows it is given, and on by default — every
	// headed column sorts unless it opts out with `sortable: false`. Making it free
	// is the point: pages get ordering without wiring anything. Server-paginated
	// tables should opt out and sort server-side, otherwise the order would only
	// hold *within the current page*, which is worse than no sorting at all.
	let {
		columns,
		rows,
		loading = false,
		empty = 'No records.',
		rowKey = undefined,
		density = 'comfortable',
		cell
	}: {
		columns: Column[];
		rows: T[];
		loading?: boolean;
		empty?: string;
		rowKey?: (row: T) => string;
		density?: Density;
		cell?: Snippet<[T, Column]>;
	} = $props();

	const align: Record<string, string> = {
		left: 'text-left',
		right: 'text-right',
		center: 'text-center'
	};

	const pad = $derived(density === 'compact' ? 'px-3 py-1' : 'px-3 py-2');

	let sortKey = $state<string | null>(null);
	let sortDir = $state<'asc' | 'desc'>('asc');

	function canSort(c: Column): boolean {
		return c.sortable !== false && c.header.trim().length > 0;
	}

	function toggleSort(c: Column) {
		if (!canSort(c)) return;
		if (sortKey === c.key) sortDir = sortDir === 'asc' ? 'desc' : 'asc';
		else {
			sortKey = c.key;
			sortDir = 'asc';
		}
	}

	function valueOf(row: T, c: Column): unknown {
		return c.sortValue ? (c.sortValue as unknown as (r: T) => unknown)(row) : row[c.key];
	}

	// Nullish always sorts last regardless of direction — an absent cell is not
	// the smallest value, it's simply missing, and burying it is what people mean.
	function compare(a: unknown, b: unknown): number {
		const aEmpty = a == null || a === '';
		const bEmpty = b == null || b === '';
		if (aEmpty || bEmpty) return aEmpty && bEmpty ? 0 : aEmpty ? 1 : -1;

		if (typeof a === 'number' && typeof b === 'number') return a - b;
		if (typeof a === 'boolean' && typeof b === 'boolean') return Number(a) - Number(b);

		// Numeric-aware collation so Gi0/2 precedes Gi0/10 and IPs read naturally.
		return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' });
	}

	const sorted = $derived.by(() => {
		const c = columns.find((x) => x.key === sortKey);
		if (!c) return rows;
		const dir = sortDir === 'asc' ? 1 : -1;
		// Copy first — sorting the prop array in place would mutate caller state.
		return [...rows].sort((x, y) => compare(valueOf(x, c), valueOf(y, c)) * dir);
	});
</script>

<div class="ui-surface overflow-x-auto">
	<table class="w-full text-sm">
		<!-- Sticky head: long lists stay readable without scrolling back up. -->
		<thead
			class="sticky top-0 z-10 border-b border-surface-200-800 bg-surface-100-900 text-surface-600-400"
		>
			<tr>
				{#each columns as c (c.key)}
					{@const sortable = canSort(c)}
					{@const active = sortKey === c.key}
					<th
						class={`${pad} font-medium ${align[c.align ?? 'left']}`}
						style={c.width ? `width:${c.width}` : undefined}
						aria-sort={active ? (sortDir === 'asc' ? 'ascending' : 'descending') : undefined}
					>
						{#if sortable}
							<button
								type="button"
								onclick={() => toggleSort(c)}
								class="inline-flex cursor-pointer items-center gap-1 transition hover:text-surface-900-100 {c.align ===
								'right'
									? 'flex-row-reverse'
									: ''}"
							>
								{c.header}
								{#if active}
									{#if sortDir === 'asc'}<ArrowUp size={11} />{:else}<ArrowDown size={11} />{/if}
								{:else}
									<ArrowUpDown size={11} class="opacity-30" />
								{/if}
							</button>
						{:else}
							{c.header}
						{/if}
					</th>
				{/each}
			</tr>
		</thead>
		<tbody>
			{#if loading}
				<TableSkeleton {columns} {density} />
			{:else if rows.length === 0}
				<tr>
					<td colspan={columns.length} class="px-3 py-2"><EmptyState title={empty} /></td>
				</tr>
			{:else}
				{#each sorted as row, i (rowKey ? rowKey(row) : i)}
					<tr class="border-b border-surface-100-900 last:border-0 hover:bg-surface-100-900/60">
						{#each columns as c (c.key)}
							<td class={`${pad} ${align[c.align ?? 'left']}`}>
								{#if cell}{@render cell(row, c)}{:else}{String(row[c.key] ?? '')}{/if}
							</td>
						{/each}
					</tr>
				{/each}
			{/if}
		</tbody>
	</table>
</div>
