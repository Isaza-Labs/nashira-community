<script lang="ts">
	// Device selector for anything that dispatches a run: the manual Run dialog, a
	// trigger's standing targets, an acceptance test. Used to be a bare checkbox list
	// duplicated per panel, which had no search and — the part that mattered — no idea
	// which devices the environment could actually reach.
	//
	// A device declares which promotion stage may dispatch to it. The executor drops
	// the rest and refuses the run when that leaves nothing, so a selection made here
	// could resolve to zero targets and only say so after the user pressed Run. The
	// gate is surfaced at selection time instead.
	import { listDevices, type Device } from '$lib/api/devices.api';
	import { SearchInput, Spinner, Alert } from '$lib/components/ui';

	let {
		selected = $bindable<string[]>([]),
		environment = '',
		disabled = false
	}: {
		selected?: string[];
		/** Drives the gate. Empty means "no environment context" — nothing is filtered. */
		environment?: string;
		disabled?: boolean;
	} = $props();

	let devices = $state<Device[]>([]);
	let loading = $state(true);
	let error = $state('');
	let search = $state('');

	const envKey = $derived.by(() => {
		const e = environment.toLowerCase();
		if (e === 'qa') return 'allowQa' as const;
		if (e === 'production') return 'allowProduction' as const;
		return e ? ('allowDraft' as const) : null;
	});

	const isTargetable = (d: Device) => !envKey || d[envKey] === true;

	const filtered = $derived.by(() => {
		const q = search.trim().toLowerCase();
		if (!q) return devices;
		return devices.filter(
			(d) =>
				d.name.toLowerCase().includes(q) ||
				d.ipAddress.toLowerCase().includes(q) ||
				d.site.toLowerCase().includes(q) ||
				d.platform.toLowerCase().includes(q)
		);
	});

	const blockedCount = $derived(devices.filter((d) => !isTargetable(d)).length);

	// Already-selected devices this environment would drop. They stay visible and
	// removable rather than trapped: a selection inherited from an edited trigger has
	// to be cleanable, and silently unselecting it would hide why the run was refused.
	const selectedButBlocked = $derived(
		devices.filter((d) => !isTargetable(d) && selected.includes(d.id))
	);

	// "Select all" operates on the currently filtered list — that is what the user
	// means by it — minus anything this environment cannot reach.
	const selectableFiltered = $derived(filtered.filter(isTargetable));
	const allFilteredSelected = $derived(
		selectableFiltered.length > 0 && selectableFiltered.every((d) => selected.includes(d.id))
	);

	$effect(() => {
		listDevices(200, 0)
			.then((r) => (devices = r.items))
			.catch((e) => (error = e instanceof Error ? e.message : 'Could not load devices'))
			.finally(() => (loading = false));
	});

	function toggle(id: string) {
		selected = selected.includes(id) ? selected.filter((s) => s !== id) : [...selected, id];
	}

	function dropBlocked() {
		const ids = new Set(selectedButBlocked.map((d) => d.id));
		selected = selected.filter((s) => !ids.has(s));
	}

	function toggleAllFiltered() {
		const ids = selectableFiltered.map((d) => d.id);
		if (allFilteredSelected) {
			selected = selected.filter((s) => !ids.includes(s));
		} else {
			selected = [...new Set([...selected, ...ids])];
		}
	}
</script>

<div class="space-y-2">
	{#if loading}
		<div class="flex justify-center py-4"><Spinner size="sm" /></div>
	{:else if error}
		<p class="text-xs text-error-600-400">{error}</p>
	{:else}
		{#if envKey && blockedCount > 0}
			<Alert tone="warning">
				<p class="text-xs">
					This workflow runs in <strong>{environment}</strong>.
					{blockedCount}
					{blockedCount === 1 ? 'device does not allow it' : 'devices do not allow it'}, so
					{blockedCount === 1 ? 'it is' : 'they are'} not selectable here. Enable
					<strong>{environment}</strong> for them on the
					<a href="/devices" class="underline">Devices</a> page, or run from an environment they
					already allow.
				</p>
			</Alert>
		{/if}

		{#if selectedButBlocked.length > 0}
			<Alert tone="error">
				<div class="flex items-start gap-2">
					<p class="flex-1 text-xs">
						{selectedButBlocked.length} selected
						{selectedButBlocked.length === 1 ? 'device does' : 'devices do'} not allow
						<strong>{environment}</strong>. The run resolves them away and is refused.
					</p>
					<button type="button" onclick={dropBlocked} class="shrink-0 text-xs underline">
						Remove them
					</button>
				</div>
			</Alert>
		{/if}

		<SearchInput bind:value={search} placeholder="Search devices…" width="w-full" />

		<label
			class="flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 transition hover:bg-surface-100-900"
		>
			<input
				type="checkbox"
				checked={allFilteredSelected}
				onchange={toggleAllFiltered}
				{disabled}
				class="h-4 w-4 rounded border-surface-300-700 accent-primary-500"
			/>
			<span class="text-xs font-medium">
				Select all{search.trim() ? ' (filtered)' : ''} ({selectableFiltered.length})
			</span>
		</label>

		<div class="max-h-60 space-y-0.5 overflow-y-auto rounded-lg border border-surface-200-800 p-1">
			{#each filtered as d (d.id)}
				{@const blocked = !isTargetable(d)}
				{@const picked = selected.includes(d.id)}
				<label
					class="flex items-center gap-2 rounded-md px-2 py-1.5 transition {blocked
						? 'opacity-60'
						: 'cursor-pointer hover:bg-surface-100-900'}"
				>
					<input
						type="checkbox"
						checked={picked}
						disabled={disabled || (blocked && !picked)}
						onchange={() => toggle(d.id)}
						class="h-4 w-4 rounded border-surface-300-700 accent-primary-500"
					/>
					<span class="min-w-0 flex-1">
						<span class="block truncate text-sm">
							{d.name}
							{#if blocked}
								<span class="ml-1 text-xs text-warning-600-400">· no {environment}</span>
							{/if}
						</span>
						<span class="block truncate text-xs text-surface-600-400">
							{d.ipAddress || 'No IP'}{d.site ? ` · ${d.site}` : ''}{d.platform
								? ` · ${d.platform}`
								: ''}
						</span>
					</span>
				</label>
			{:else}
				<p class="px-2 py-3 text-center text-xs text-surface-600-400">
					{devices.length === 0 ? 'No devices in inventory.' : 'No devices match.'}
				</p>
			{/each}
		</div>

		{#if selected.length > 0}
			<p class="px-1 text-xs text-surface-600-400">
				{selected.length}
				{selected.length === 1 ? 'device' : 'devices'} selected
			</p>
		{/if}
	{/if}
</div>
