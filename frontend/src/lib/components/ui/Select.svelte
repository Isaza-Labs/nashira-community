<script lang="ts">
	let {
		value = $bindable(''),
		label = '',
		id = undefined,
		options = [],
		placeholder = undefined,
		hint = '',
		error = '',
		required = false,
		disabled = false
	}: {
		value?: string;
		label?: string;
		id?: string;
		options?: { value: string; label: string }[];
		placeholder?: string;
		hint?: string;
		error?: string;
		required?: boolean;
		disabled?: boolean;
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
	<select
		id={controlId}
		{required}
		{disabled}
		bind:value
		aria-invalid={error ? 'true' : undefined}
		class={`ui-control w-full px-3 py-2 outline-none ${
			error ? '!border-error-500' : ''
		} disabled:opacity-60`}
	>
		{#if placeholder !== undefined}
			<option value="" disabled>{placeholder}</option>
		{/if}
		{#each options as o (o.value)}
			<option value={o.value}>{o.label}</option>
		{/each}
	</select>
	{#if error}
		<p class="text-xs text-error-600-400">{error}</p>
	{:else if hint}
		<p class="text-xs text-surface-600-400">{hint}</p>
	{/if}
</div>
