<script lang="ts">
	import { page } from '$app/state';
	import {
		listPools,
		createPool,
		updatePool,
		deletePool,
		resolveMembers,
		type DevicePool,
		type DevicePoolPayload,
		type PoolResolution
	} from '$lib/api/pools.api';
	import { listDevices, type Device } from '$lib/api/devices.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		IconButton,
		Input,
		Textarea,
		Select,
		CodeEditor,
		Checkbox,
		Alert,
		Spinner,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { Plus, Pencil, Trash2, List } from 'lucide-svelte';

	let items = $state<DevicePool[]>([]);
	let devices = $state<Device[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<DevicePool | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fDescription = $state('');
	let fRules = $state('');
	let fMembers = $state<string[]>([]);
	let fAllowDraft = $state(true);
	let fAllowQa = $state(false);
	let fAllowProduction = $state(true);

	let membersOpen = $state(false);
	let membersFor = $state<DevicePool | null>(null);
	let resolution = $state<PoolResolution | null>(null);
	let membersLoading = $state(false);
	let membersEnv = $state('');

	const envOptions = [
		{ value: '', label: '— all members (no environment filter) —' },
		{ value: 'draft', label: 'draft' },
		{ value: 'qa', label: 'qa' },
		{ value: 'production', label: 'production' }
	];

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'definition', header: 'Definition', sortable: false },
		{ key: 'members', header: 'Members', sortValue: (p: DevicePool) => p.memberCount },
		{ key: 'environments', header: 'Environments', sortable: false },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listPools()).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
		listDevices(200, 0)
			.then((r) => (devices = r.items))
			.catch(() => (devices = []));
	});

	function reset() {
		formError = '';
		fName = '';
		fDescription = '';
		fRules = '';
		fMembers = [];
		fAllowDraft = true;
		fAllowQa = false;
		fAllowProduction = true;
	}

	function openCreate() {
		editing = null;
		reset();
		modalOpen = true;
	}

	function openEdit(p: DevicePool) {
		editing = p;
		formError = '';
		fName = p.name;
		fDescription = p.description ?? '';
		fRules = p.filterRules ?? '';
		fMembers = [...p.staticMembers];
		fAllowDraft = p.allowDraft;
		fAllowQa = p.allowQa;
		fAllowProduction = p.allowProduction;
		modalOpen = true;
	}

	function toggleMember(id: string) {
		fMembers = fMembers.includes(id) ? fMembers.filter((m) => m !== id) : [...fMembers, id];
	}

	function payload(): DevicePoolPayload {
		return {
			name: fName.trim(),
			description: fDescription,
			filterRules: fRules,
			staticMembers: fMembers,
			allowDraft: fAllowDraft,
			allowQa: fAllowQa,
			allowProduction: fAllowProduction
		};
	}

	async function save() {
		if (!fName.trim()) {
			formError = 'Name is required.';
			return;
		}
		formError = '';
		saving = true;
		try {
			if (editing) await updatePool(editing.id, payload());
			else await createPool(payload());
			modalOpen = false;
			toast.success(editing ? 'Pool updated' : 'Pool created');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the pool');
		} finally {
			saving = false;
		}
	}

	async function remove(p: DevicePool) {
		const ok = await confirm({
			title: 'Delete pool?',
			message: `"${p.name}" will be removed. The devices themselves are untouched.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deletePool(p.id);
			items = items.filter((x) => x.id !== p.id);
			toast.success('Pool deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the pool");
		}
	}

	function openMembers(p: DevicePool) {
		membersFor = p;
		membersEnv = '';
		resolution = null;
		membersOpen = true;
	}

	// Re-resolves whenever the drawer opens or the environment picker changes.
	// Reactive rather than an onchange handler because Select does not expose one.
	$effect(() => {
		const pool = membersFor;
		const env = membersEnv;
		if (!membersOpen || !pool) return;

		let cancelled = false;
		membersLoading = true;
		resolveMembers(pool.id, env || undefined)
			.then((r) => {
				if (!cancelled) resolution = r;
			})
			.catch((e) => {
				if (!cancelled) toast.fromError(e, "Couldn't resolve the pool");
			})
			.finally(() => {
				if (!cancelled) membersLoading = false;
			});

		// A fast switch between environments must not let a slow earlier response
		// overwrite the newer one.
		return () => {
			cancelled = true;
		};
	});

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Device pools · Nashira</title></svelte:head>

<PageHeader
	title="Device pools"
	description="Named groups of devices, by explicit members and by rules."
>
	{#snippet actions()}
		<RoleGate require="operator">
			<Button variant="primary" onclick={openCreate}><Plus size={15} />New pool</Button>
		</RoleGate>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<DataTable {loading} {columns} rows={items} rowKey={(p) => p.id} empty="No pools yet — group devices once so workflows can target ‘core routers’ instead of a list that ages.">
		{#snippet cell(row, col)}
			{#if col.key === 'name'}
				<div class="font-medium">{row.name}</div>
				<div class="text-xs text-surface-600-400">{row.slug}</div>
			{:else if col.key === 'definition'}
				<div class="flex flex-wrap gap-1">
					{#if row.filterRules}<Badge>rules</Badge>{/if}
					{#if row.staticMembers.length > 0}
						<Badge tone="neutral">{row.staticMembers.length} pinned</Badge>
					{/if}
					{#if !row.filterRules && row.staticMembers.length === 0}
						<span class="text-xs text-warning-600-400">empty — matches nothing</span>
					{/if}
				</div>
			{:else if col.key === 'members'}
				<span class="tabular-nums text-surface-600-400">{row.memberCount}</span>
			{:else if col.key === 'environments'}
				<div class="flex flex-wrap gap-1">
					{#if row.allowDraft}<Badge tone="neutral">draft</Badge>{/if}
					{#if row.allowQa}<Badge tone="warning">qa</Badge>{/if}
					{#if row.allowProduction}<Badge tone="error">production</Badge>{/if}
					{#if !row.allowDraft && !row.allowQa && !row.allowProduction}
						<Badge tone="warning">parked</Badge>
					{/if}
				</div>
			{:else if col.key === 'actions'}
				<div class="flex justify-end gap-1">
					<IconButton label={`Resolve ${row.name}`} onclick={() => openMembers(row)}>
						<List size={14} />
					</IconButton>
					<RoleGate require="operator">
						<IconButton label="Edit pool" onclick={() => openEdit(row)}><Pencil size={14} /></IconButton>
						<IconButton label="Delete pool" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
					</RoleGate>
				</div>
			{/if}
		{/snippet}
	</DataTable>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit pool' : 'New pool'} size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		<Input label="Name" bind:value={fName} required />
		<Textarea label="Description" bind:value={fDescription} rows={2} />

		<CodeEditor
			label="Filter rules"
			language="json"
			bind:value={fRules}
			rows={5}
			hint={'JSON object. Keys: site, role, vendor, platform, status. Every key must match (AND).'}
		/>

		<div>
			<div class="mb-1.5 text-sm font-medium">Pinned members</div>
			<p class="mb-2 text-xs text-surface-600-400">
				Always in the pool regardless of the rules.
			</p>
			<div class="max-h-48 space-y-1 overflow-y-auto rounded-lg border border-surface-200-800 p-2">
				{#each devices as d (d.id)}
					<label class="flex items-center gap-2 px-1 py-0.5 text-sm">
						<input
							type="checkbox"
							checked={fMembers.includes(d.id)}
							onchange={() => toggleMember(d.id)}
							class="h-4 w-4 rounded border-surface-300-700 accent-primary-500 dark:accent-primary-400"
						/>
						<span>{d.name} <span class="text-xs text-surface-600-400">({d.ipAddress})</span></span>
					</label>
				{:else}
					<p class="p-2 text-xs text-surface-600-400">No devices in inventory.</p>
				{/each}
			</div>
		</div>

		<fieldset class="space-y-2 rounded-lg border border-surface-200-800 p-3">
			<legend class="px-1 text-sm font-medium">Environments</legend>
			<p class="text-xs text-surface-600-400">
				Applied on top of each member's own trio — a run must be allowed by both.
			</p>
			<div class="flex flex-wrap gap-4 pt-1">
				<Checkbox bind:checked={fAllowDraft} label="Draft" />
				<Checkbox bind:checked={fAllowQa} label="QA" />
				<Checkbox bind:checked={fAllowProduction} label="Production" />
			</div>
		</fieldset>

		{#if !fRules.trim() && fMembers.length === 0}
			<Alert tone="warning">
				With no rules and no pinned members this pool matches nothing.
			</Alert>
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>

<Modal bind:open={membersOpen} title={`${membersFor?.name ?? ''} — members`} size="lg">
	<div class="space-y-3">
		<Select label="Environment" bind:value={membersEnv} options={envOptions} />

		{#if membersLoading}
			<div class="flex justify-center py-10"><Spinner /></div>
		{:else if resolution}
			{#if membersEnv && !resolution.poolAllowsEnvironment}
				<Alert tone="error">
					This pool does not allow the <strong>{membersEnv}</strong> environment, so no run in it can
					target any member.
				</Alert>
			{/if}

			{#if resolution.members.length === 0}
				<p class="px-4 py-8 text-center text-sm text-surface-600-400">No members resolve.</p>
			{:else}
				<div class="max-h-72 space-y-1 overflow-y-auto">
					{#each resolution.members as m (m.deviceId)}
						<div class="flex items-center gap-3 rounded-lg border border-surface-100-900 px-3 py-2">
							<span class="flex-1 text-sm font-medium">{m.name}</span>
							<code class="text-xs text-surface-600-400">{m.ip}</code>
						</div>
					{/each}
				</div>
			{/if}

			{#if resolution.excluded.length > 0}
				<Alert tone="warning">
					<div class="font-medium">Excluded by the environment</div>
					<ul class="mt-1 list-inside list-disc text-xs">
						{#each resolution.excluded as reason (reason)}<li>{reason}</li>{/each}
					</ul>
				</Alert>
			{/if}
		{/if}
	</div>
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (membersOpen = false)}>Close</Button>
	{/snippet}
</Modal>
