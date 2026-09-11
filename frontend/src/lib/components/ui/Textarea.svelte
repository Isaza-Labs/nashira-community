<script lang="ts">
	let {
		value = $bindable(''),
		label = '',
		id = undefined,
		placeholder = '',
		hint = '',
		error = '',
		rows = 4,
		required = false,
		disabled = false,
		...rest
	}: {
		value?: string;
		label?: string;
		id?: string;
		placeholder?: string;
		hint?: string;
		error?: string;
		rows?: number;
		required?: boolean;
		disabled?: boolean;
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
	<textarea
		id={controlId}
		{placeholder}
		{rows}
		{required}
		{disabled}
		bind:value
		aria-invalid={error ? 'true' : undefined}
		class={`ui-control w-full px-3 py-2 outline-none ${
			error ? '!border-error-500' : ''
		} disabled:opacity-60`}
		{...rest}
	></textarea>
	{#if error}
		<p class="text-xs text-error-600-400">{error}</p>
	{:else if hint}
		<p class="text-xs text-surface-600-400">{hint}</p>
	{/if}
</div>
