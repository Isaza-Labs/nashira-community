<script lang="ts">
	import { Search, X } from 'lucide-svelte';

	// Compact search field with a leading icon and a clear button. Bind `value`
	// and/or react via `onInput`.
	let {
		value = $bindable(''),
		placeholder = 'Search…',
		width = 'w-64',
		onInput
	}: {
		value?: string;
		placeholder?: string;
		width?: string;
		onInput?: (v: string) => void;
	} = $props();

	function handleInput(e: Event) {
		const v = (e.target as HTMLInputElement).value;
		value = v;
		onInput?.(v);
	}

	function clear() {
		value = '';
		onInput?.('');
	}
</script>

<div class="relative {width}">
	<span class="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-surface-600-400">
		<Search size={14} />
	</span>
	<input
		type="text"
		role="searchbox"
		aria-label={placeholder}
		{value}
		{placeholder}
		oninput={handleInput}
		class="h-8 w-full rounded-md border border-surface-300-700 bg-surface-50-950 pl-8 pr-8 text-sm text-surface-900-100 transition-colors placeholder:text-surface-600-400 focus:border-primary-500 focus:outline-none focus:ring-2 focus:ring-primary-500/30 dark:focus:border-primary-400 dark:focus:ring-primary-400/30"
	/>
	{#if value}
		<button
			type="button"
			onclick={clear}
			aria-label="Clear search"
			class="absolute right-2 top-1/2 -translate-y-1/2 text-surface-600-400 transition-colors hover:text-surface-800-200"
		>
			<X size={14} />
		</button>
	{/if}
</div>
