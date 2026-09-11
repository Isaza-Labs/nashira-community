<script lang="ts">
	// Controlled tab strip. Bind `value` to the active tab key; render the panel
	// yourself based on it.
	//
	// `onchange` is for callers that have work to do on the switch itself — clearing
	// feedback that belonged to the tab being left, say. A binding alone gives no
	// hook for that, and reacting to the value afterwards cannot tell a user's click
	// apart from a programmatic reset.
	let {
		tabs,
		value = $bindable(''),
		onchange
	}: {
		tabs: { value: string; label: string }[];
		value?: string;
		onchange?: (value: string) => void;
	} = $props();

	$effect(() => {
		if (!value && tabs.length) value = tabs[0].value;
	});

	function select(next: string) {
		if (next === value) return;
		value = next;
		onchange?.(next);
	}
</script>

<div class="flex gap-1 border-b border-surface-200-800" role="tablist">
	{#each tabs as t (t.value)}
		<button
			type="button"
			role="tab"
			aria-selected={value === t.value}
			class={`-mb-px border-b-2 px-3 py-2 text-sm transition ${
				value === t.value
					? 'border-primary-500 text-surface-950-50 dark:border-primary-400'
					: 'border-transparent text-surface-600-400 hover:text-surface-950-50'
			}`}
			onclick={() => select(t.value)}
		>
			{t.label}
		</button>
	{/each}
</div>
