<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { Tone } from './types';

	let { tone = 'neutral', children }: { tone?: Tone; children?: Snippet } = $props();

	const tones: Record<Tone, string> = {
		neutral: 'bg-surface-200-800 text-surface-700-300',
		primary: 'bg-primary-500/15 text-primary-700-300',
		success: 'bg-success-500/15 text-success-700-300',
		warning: 'bg-warning-500/15 text-warning-700-300',
		error: 'bg-error-500/15 text-error-700-300'
	};
</script>

<span
	class={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${tones[tone]}`}
>
	{@render children?.()}
</span>
