<script lang="ts">
	import type { Size } from './types';

	let { size = 'md', class: klass = '' }: { size?: Size; class?: string } = $props();

	const px: Record<Size, string> = { sm: 'h-4 w-4', md: 'h-5 w-5', lg: 'h-8 w-8' };
</script>

<svg
	class={`animate-spin ${px[size]} ${klass}`}
	viewBox="0 0 24 24"
	fill="none"
	role="status"
	aria-label="Loading"
>
	<circle class="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" stroke-width="4" />
	<path class="opacity-90" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z" />
</svg>
