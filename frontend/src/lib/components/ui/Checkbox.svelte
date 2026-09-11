<script lang="ts">
	// `ariaLabel` names a checkbox that carries no visible text — a per-row selector in
	// a list, where the visible name belongs to the row rather than to the control.
	// Without it such a checkbox reaches a screen reader as an unnamed control.
	let {
		checked = $bindable(false),
		label = '',
		ariaLabel = undefined,
		id = undefined,
		disabled = false
	}: {
		checked?: boolean;
		label?: string;
		ariaLabel?: string;
		id?: string;
		disabled?: boolean;
	} = $props();
</script>

<label for={id} class="inline-flex items-center gap-2 text-sm" class:opacity-60={disabled}>
	<input
		{id}
		type="checkbox"
		{disabled}
		aria-label={ariaLabel}
		bind:checked
		class="h-4 w-4 rounded border-surface-300-700 accent-primary-500"
	/>
	{#if label}<span>{label}</span>{/if}
</label>
