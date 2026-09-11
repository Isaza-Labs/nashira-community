<script lang="ts">
	import type { Snippet } from 'svelte';

	// Inline keyboard-key label, e.g. <Kbd>Ctrl</Kbd>.
	let { children }: { children: Snippet } = $props();
</script>

<kbd
	class="inline-flex h-5 min-w-[1.25rem] items-center justify-center rounded border border-surface-300-700 bg-surface-200-800 px-1.5 font-mono text-[10px] font-medium text-surface-700-300"
>
	{@render children()}
</kbd>
