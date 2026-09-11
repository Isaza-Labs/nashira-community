<script lang="ts">
	import { X } from 'lucide-svelte';

	// A visible, removable statement of "you are looking at a subset".
	//
	// Arriving at a filtered list through a link is only safe if the filter is
	// obvious and reversible: a list that silently shows 3 of 40 rows reads as a
	// list with 3 rows, and the next person swears the data is missing.
	let {
		label,
		value,
		clearHref,
		count = undefined
	}: {
		/** What is being filtered on, e.g. "Integration". */
		label: string;
		/** The human-readable value, e.g. the integration name. */
		value: string;
		/** Where "clear" goes — normally the same route without the query. */
		clearHref: string;
		/** How many rows survived the filter, when the caller knows. */
		count?: number;
	} = $props();
</script>

<div class="flex items-center gap-2 text-sm">
	<span
		class="inline-flex items-center gap-1.5 rounded-full bg-primary-500/12 py-1 pl-3 pr-1 text-primary-700-300 ring-1 ring-inset ring-primary-500/25"
	>
		<span class="text-xs">
			{label}: <strong class="font-medium">{value}</strong>
			{#if count !== undefined}
				<span class="text-primary-700-300/70">· {count} shown</span>
			{/if}
		</span>
		<a
			href={clearHref}
			aria-label={`Clear the ${label.toLowerCase()} filter`}
			title="Clear filter"
			class="inline-flex h-5 w-5 items-center justify-center rounded-full transition hover:bg-primary-500/20"
		>
			<X size={12} />
		</a>
	</span>
</div>
