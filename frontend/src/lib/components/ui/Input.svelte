<script lang="ts">
	import type { HTMLInputAttributes } from 'svelte/elements';

	// Labeled text input. Uses one-way value + oninput (not bind:value) so `type`
	// can stay dynamic — Svelte disallows bind:value with a dynamic type.
	let {
		value = $bindable(''),
		label = '',
		id = undefined,
		type = 'text',
		placeholder = '',
		hint = '',
		error = '',
		required = false,
		disabled = false,
		autocomplete = undefined,
		...rest
	}: {
		value?: string;
		label?: string;
		id?: string;
		type?: string;
		placeholder?: string;
		hint?: string;
		error?: string;
		required?: boolean;
		disabled?: boolean;
		autocomplete?: HTMLInputAttributes['autocomplete'];
		[key: string]: unknown;
	} = $props();

	// Auto-associate label and control when no explicit id is given (a11y).
	const uid = $props.id();
	const controlId = $derived(id ?? uid);
</script>

<div class="space-y-1">
	{#if label}
		<label for={controlId} class="text-sm font-medium">
			{label}{#if required}<span class="text-error-500"> *</span>{/if}
		</label>
	{/if}
	<input
		id={controlId}
		{type}
		{placeholder}
		{required}
		{disabled}
		{autocomplete}
		value={value}
		oninput={(e) => (value = (e.currentTarget as HTMLInputElement).value)}
		aria-invalid={error ? 'true' : undefined}
		class={`ui-control w-full px-3 py-2 outline-none ${
			error ? '!border-error-500' : ''
		} disabled:opacity-60`}
		{...rest}
	/>
	{#if error}
		<p class="text-xs text-error-600-400">{error}</p>
	{:else if hint}
		<p class="text-xs text-surface-600-400">{hint}</p>
	{/if}
</div>
