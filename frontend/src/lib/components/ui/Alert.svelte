<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { Tone } from './types';

	let {
		tone = 'primary',
		title = '',
		dismissible = false,
		ondismiss = undefined,
		children
	}: {
		tone?: Tone;
		title?: string;
		dismissible?: boolean;
		ondismiss?: () => void;
		children?: Snippet;
	} = $props();

	const tones: Record<Tone, string> = {
		neutral: 'border-surface-300-700 bg-surface-100-900 text-surface-800-200',
		primary: 'border-primary-500/40 bg-primary-500/10 text-primary-800-200',
		success: 'border-success-500/40 bg-success-500/10 text-success-800-200',
		warning: 'border-warning-500/40 bg-warning-500/10 text-warning-800-200',
		error: 'border-error-500/40 bg-error-500/10 text-error-800-200'
	};
	// Errors/warnings interrupt (assertive); info/success are polite.
	const role = $derived(tone === 'error' || tone === 'warning' ? 'alert' : 'status');
</script>

<div class={`flex gap-3 rounded-md border px-3 py-2 text-sm ${tones[tone]}`} {role}>
	<div class="flex-1">
		{#if title}<div class="font-medium">{title}</div>{/if}
		<div class:mt-0.5={title}>{@render children?.()}</div>
	</div>
	{#if dismissible}
		<button
			type="button"
			class="shrink-0 rounded p-0.5 leading-none opacity-70 hover:opacity-100"
			aria-label="Dismiss"
			onclick={ondismiss}
		>
			&times;
		</button>
	{/if}
</div>
