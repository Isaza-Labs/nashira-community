<script lang="ts">
	import { Badge, Button, Modal, SearchInput, StatusBadge } from '$lib/components/ui';
	import type { PythonEnvironment } from '$lib/api/python-environments.api';
	import { X } from 'lucide-svelte';

	// Picks any number of uv environments (Admin → Python → Environments) without the
	// form growing with them: the field is a short summary of chips plus a button, and
	// the full list lives in a dialog stacked over whatever modal hosts the field. The
	// native <dialog> behind Modal puts the second one in the top layer above the
	// first, so Esc and the backdrop close only the picker. Edits in the dialog are a
	// draft — Cancel (or Esc, ×, the backdrop) drops them; Apply writes them to `value`.
	let {
		value = $bindable<string[]>([]),
		environments,
		label = 'Environments',
		maxChips = 5
	}: {
		value?: string[];
		environments: PythonEnvironment[];
		label?: string;
		/** How many selected environments are shown as chips before "+N more". */
		maxChips?: number;
	} = $props();

	let open = $state(false);
	let query = $state('');
	let draft = $state<string[]>([]);

	// An id with no environment behind it (deleted elsewhere, or the list failed to
	// load) still gets a chip, so it is never dropped without the user seeing it.
	type Chip = { id: string; env: PythonEnvironment | null; name: string };
	const selected = $derived<Chip[]>(
		value.map((id) => {
			const env = environments.find((e) => e.id === id) ?? null;
			return { id, env, name: env ? env.name : `unknown (${id.slice(0, 8)}…)` };
		})
	);
	const shown = $derived(selected.slice(0, maxChips));
	const hidden = $derived(selected.slice(maxChips));

	const visible = $derived.by(() => {
		const q = query.trim().toLowerCase();
		if (!q) return environments;
		return environments.filter(
			(e) => e.name.toLowerCase().includes(q) || (e.description ?? '').toLowerCase().includes(q)
		);
	});
	const draftCount = $derived(draft.length);

	function openPicker() {
		draft = [...value];
		query = '';
		open = true;
	}

	function toggle(id: string, on: boolean) {
		draft = on ? [...draft.filter((x) => x !== id), id] : draft.filter((x) => x !== id);
	}

	function selectAllVisible() {
		const add = visible.map((e) => e.id).filter((id) => !draft.includes(id));
		draft = [...draft, ...add];
	}

	function clear() {
		draft = [];
	}

	function apply() {
		value = [...draft];
		open = false;
	}

	function remove(id: string) {
		value = value.filter((x) => x !== id);
	}
</script>

<div class="space-y-2">
	{#if selected.length > 0}
		<ul class="flex flex-wrap gap-1.5" aria-label={`Selected ${label.toLowerCase()}`}>
			{#each shown as chip (chip.id)}
				<li
					class="inline-flex max-w-full items-center gap-1.5 rounded-full border border-surface-200-800 bg-surface-100-900 py-0.5 pl-2.5 pr-1 text-xs"
					title={chip.env
						? chip.env.description
							? `${chip.name} — ${chip.env.description}`
							: chip.name
						: `Environment ${chip.id} is not in the current list`}
				>
					<span
						class="truncate font-medium {chip.env
							? 'text-surface-900-100'
							: 'italic text-surface-600-400'}">{chip.name}</span
					>
					{#if chip.env}<StatusBadge status={chip.env.status} />{/if}
					<button
						type="button"
						class="rounded-full p-0.5 text-surface-600-400 transition-colors hover:bg-surface-200-800 hover:text-surface-900-100"
						aria-label={`Remove ${chip.name}`}
						onclick={() => remove(chip.id)}
					>
						<X size={12} />
					</button>
				</li>
			{/each}
			{#if hidden.length > 0}
				<li
					class="inline-flex items-center rounded-full bg-surface-200-800 px-2.5 py-0.5 text-xs font-medium text-surface-700-300"
					title={hidden.map((c) => (c.env ? `${c.name} (${c.env.status})` : c.name)).join('\n')}
				>
					+{hidden.length} more
				</li>
			{/if}
		</ul>
	{/if}

	<div class="flex items-center gap-2">
		<Button size="sm" variant="secondary" disabled={environments.length === 0} onclick={openPicker}>
			Choose environments…
		</Button>
		<span class="text-xs text-surface-600-400">
			{environments.length === 0
				? 'No environments'
				: `${selected.filter((c) => c.env).length} of ${environments.length} selected`}
		</span>
	</div>
</div>

<Modal bind:open title="Choose environments" size="md">
	<div class="space-y-3">
		<SearchInput bind:value={query} placeholder="Search by name or description" width="w-full" />

		<div class="flex items-center justify-between gap-2 text-xs">
			<span class="text-surface-600-400" aria-live="polite">{draftCount} selected</span>
			<div class="flex gap-1">
				<Button size="sm" variant="ghost" disabled={visible.length === 0} onclick={selectAllVisible}>
					Select all visible
				</Button>
				<Button size="sm" variant="ghost" disabled={draft.length === 0} onclick={clear}>Clear</Button>
			</div>
		</div>

		<ul
			class="max-h-72 divide-y divide-surface-200-800 overflow-y-auto rounded-md border border-surface-200-800"
		>
			{#each visible as env (env.id)}
				<li>
					<label
						class="flex cursor-pointer items-start gap-3 px-3 py-2 hover:bg-surface-100-900"
					>
						<input
							type="checkbox"
							class="mt-0.5 h-4 w-4 shrink-0 rounded border-surface-300-700 accent-primary-500"
							checked={draft.includes(env.id)}
							onchange={(e) => toggle(env.id, (e.currentTarget as HTMLInputElement).checked)}
						/>
						<span class="min-w-0 flex-1">
							<span class="flex flex-wrap items-center gap-2">
								<span class="text-sm font-medium text-surface-900-100">{env.name}</span>
								<StatusBadge status={env.status} />
								{#if env.isSshFallback}
									<Badge tone="primary">SSH fallback</Badge>
								{/if}
							</span>
							{#if env.description}
								<span class="block truncate text-xs text-surface-600-400" title={env.description}>
									{env.description}
								</span>
							{/if}
						</span>
					</label>
				</li>
			{:else}
				<li class="px-3 py-6 text-center text-xs text-surface-600-400">
					No environment matches “{query.trim()}”.
				</li>
			{/each}
		</ul>
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (open = false)}>Cancel</Button>
		<Button variant="primary" onclick={apply}>Apply</Button>
	{/snippet}
</Modal>
