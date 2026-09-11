<script lang="ts">
	// A searchable multi-select that renders its selection as removable chips.
	//
	// Type to filter values that actually exist — device roles, pool names, snippet
	// types — and click or press Enter to add one. No free typing on purpose: a rule
	// naming a role nobody uses never matches, and a policy that never matches is
	// indistinguishable from no policy at all.
	//
	// Presentational. The parent loads `options` and owns `values`; a value that is
	// selected but no longer offered (a pool renamed after the rule was written) still
	// renders as a removable chip rather than vanishing, because silently dropping it
	// would change the rule without saying so.
	import { X, Search } from 'lucide-svelte';

	let {
		label,
		options = [],
		values = $bindable<string[]>([]),
		hint = '',
		emptyHint = 'None available'
	}: {
		label: string;
		options?: { value: string; label: string }[];
		values?: string[];
		hint?: string;
		/** Shown in the field when nothing can be picked, e.g. an empty inventory. */
		emptyHint?: string;
	} = $props();

	// Ids for the input/listbox pair; `label` can contain spaces, so it cannot be one.
	const uid = $props.id();

	let query = $state('');
	let open = $state(false);

	const available = $derived(options.filter((o) => !values.includes(o.value)));
	const filtered = $derived.by(() => {
		const q = query.trim().toLowerCase();
		if (!q) return available;
		return available.filter((o) => o.label.toLowerCase().includes(q));
	});

	function labelFor(value: string): string {
		return options.find((o) => o.value === value)?.label ?? value;
	}

	function pick(value: string) {
		if (!values.includes(value)) values = [...values, value];
		query = '';
	}

	function remove(value: string) {
		values = values.filter((v) => v !== value);
	}

	function onkeydown(e: KeyboardEvent) {
		if (e.key === 'Enter') {
			e.preventDefault();
			if (filtered.length > 0) pick(filtered[0].value);
		} else if (e.key === 'Escape') {
			open = false;
			query = '';
		}
	}
</script>

<div class="space-y-1">
	<span class="text-sm font-medium">{label}</span>

	{#if values.length > 0}
		<div class="flex flex-wrap items-center gap-1.5">
			{#each values as value (value)}
				<span
					class="inline-flex items-center gap-1 rounded-full border border-surface-300-700 bg-surface-100-900 py-0.5 pl-2.5 pr-1 text-xs"
				>
					{labelFor(value)}
					<button
						type="button"
						onclick={() => remove(value)}
						aria-label={`Remove ${labelFor(value)}`}
						class="inline-flex h-4 w-4 items-center justify-center rounded-full transition hover:bg-surface-300-700"
					>
						<X size={10} />
					</button>
				</span>
			{/each}
		</div>
	{/if}

	<div class="relative">
		<span
			class="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-surface-600-400"
		>
			<Search size={14} />
		</span>
		<input
			type="text"
			role="combobox"
			aria-expanded={open}
			aria-controls="{uid}-options"
			aria-label={label}
			bind:value={query}
			disabled={options.length === 0}
			placeholder={options.length === 0 ? emptyHint : 'Search…'}
			onfocus={() => (open = true)}
			onblur={() => (open = false)}
			{onkeydown}
			class="ui-control w-full py-2 pl-8 pr-3 outline-none disabled:opacity-60"
		/>

		{#if open && options.length > 0}
			<!-- onmousedown + preventDefault keeps focus on the input, so the blur that
			     closes this list does not fire before the click registers. -->
			<div
				id="{uid}-options"
				class="ui-surface absolute z-20 mt-1 max-h-52 w-full overflow-y-auto p-1"
			>
				{#each filtered as o (o.value)}
					<button
						type="button"
						onmousedown={(e) => {
							e.preventDefault();
							pick(o.value);
						}}
						class="block w-full truncate rounded-md px-2.5 py-1.5 text-left text-sm transition hover:bg-surface-100-900"
					>
						{o.label}
					</button>
				{:else}
					<p class="px-2.5 py-2 text-xs text-surface-600-400">
						{available.length === 0 ? 'All added' : 'No matches'}
					</p>
				{/each}
			</div>
		{/if}
	</div>

	{#if hint}<p class="text-xs text-surface-600-400">{hint}</p>{/if}
</div>
