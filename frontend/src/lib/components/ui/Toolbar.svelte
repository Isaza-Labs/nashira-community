<script lang="ts">
	import type { Snippet } from 'svelte';

	// Thin flex row for above-table controls: `left`/children on the start,
	// `right` on the end, wrapping on small screens.
	let {
		left,
		right,
		children
	}: {
		left?: Snippet;
		right?: Snippet;
		children?: Snippet;
	} = $props();
</script>

<div class="flex flex-wrap items-center justify-between gap-3">
	<div class="flex flex-wrap items-center gap-2">
		{#if left}{@render left()}{/if}
		{#if children}{@render children()}{/if}
	</div>
	{#if right}
		<div class="flex flex-wrap items-center gap-2">{@render right()}</div>
	{/if}
</div>
