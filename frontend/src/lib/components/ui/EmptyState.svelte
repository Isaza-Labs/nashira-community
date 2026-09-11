<script lang="ts">
	import type { Snippet } from 'svelte';

	let {
		title,
		description = '',
		actions
	}: { title: string; description?: string; actions?: Snippet } = $props();
</script>

<div class="flex flex-col items-center justify-center gap-2 py-10 text-center">
	<p class="font-medium text-surface-700-300">{title}</p>
	{#if description}<p class="max-w-sm text-sm text-surface-600-400">{description}</p>{/if}
	{#if actions}<div class="mt-2">{@render actions()}</div>{/if}
</div>
