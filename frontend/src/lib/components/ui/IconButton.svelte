<script lang="ts">
	import type { Snippet } from 'svelte';

	// Square, icon/glyph-only button. `label` is required for accessibility
	// (aria-label + title) since there is no visible text.
	let {
		label,
		onclick = undefined,
		variant = 'ghost',
		disabled = false,
		children
	}: {
		label: string;
		onclick?: (e: MouseEvent) => void;
		variant?: 'ghost' | 'secondary';
		disabled?: boolean;
		children?: Snippet;
	} = $props();

	const v = $derived(
		variant === 'secondary'
			? 'border border-surface-300-700 bg-surface-100-900 hover:bg-surface-200-800'
			: 'hover:bg-surface-100-900'
	);
</script>

<button
	type="button"
	aria-label={label}
	title={label}
	{disabled}
	{onclick}
	class={`inline-flex h-9 w-9 items-center justify-center rounded-md text-surface-600-400 transition disabled:opacity-60 ${v}`}
>
	{@render children?.()}
</button>
