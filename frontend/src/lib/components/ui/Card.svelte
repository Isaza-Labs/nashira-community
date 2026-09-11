<script lang="ts">
	import type { Snippet } from 'svelte';

	let {
		title = '',
		header,
		footer,
		class: klass = '',
		children
	}: {
		title?: string;
		header?: Snippet;
		footer?: Snippet;
		class?: string;
		children?: Snippet;
	} = $props();
</script>

<div class={`ui-surface ${klass}`}>
	{#if header || title}
		<div class="border-b border-surface-200-800 px-4 py-3">
			{#if header}{@render header()}{:else}<h3 class="font-medium">{title}</h3>{/if}
		</div>
	{/if}
	<div class="p-4">
		{@render children?.()}
	</div>
	{#if footer}
		<div class="border-t border-surface-200-800 px-4 py-3">{@render footer()}</div>
	{/if}
</div>
