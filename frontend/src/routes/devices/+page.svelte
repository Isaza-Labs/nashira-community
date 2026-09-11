<script lang="ts">
	import { untrack } from 'svelte';
	import { page } from '$app/state';
	import {
		listDevices,
		createDevice,
		updateDevice,
		deleteDevice,
		type Device,
		type DevicePayload
	} from '$lib/api/devices.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		StatusBadge,
		IconButton,
		Toolbar,
		SearchInput,
		Pagination,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import DeviceForm from '$lib/components/device/DeviceForm.svelte';
	import { Plus, Pencil, Trash2 } from 'lucide-svelte';

	let items = $state<Device[]>([]);
	let total = $state(0);
	let loading = $state(true);
	let error = $state<unknown>(null);
	let search = $state('');
	let offset = $state(0);
	const limit = 50;

	// Drops responses that arrive out of order while typing in the search box.
	let seq = 0;

	let modalOpen = $state(false);
	let editing = $state<Device | null>(null);
	let saving = $state(false);

	// Client-side sorting is off: the table shows one server page at a time, and an
	// order that only holds within the visible page would mislead. The server sorts
	// by device name.
	const columns: Column[] = [
		{ key: 'name', header: 'Name', sortable: false },
		{ key: 'ipAddress', header: 'IP address', sortable: false },
		{ key: 'site', header: 'Site', sortable: false },
		{ key: 'role', header: 'Role', sortable: false },
		{ key: 'status', header: 'Status', sortable: false },
		{ key: 'environments', header: 'Environments', sortable: false },
		{ key: 'source', header: 'Source', sortable: false },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	// The stages a device accepts, in promotion order.
	function environmentsOf(d: Device): string[] {
		const names: string[] = [];
		if (d.allowDraft) names.push('draft');
		if (d.allowQa) names.push('qa');
		if (d.allowProduction) names.push('production');
		return names;
	}

	async function load() {
		const mine = ++seq;
		loading = true;
		error = null;
		try {
			const page = await listDevices(limit, offset, search.trim() || undefined);
			if (mine !== seq) return;
			items = page.items;
			total = page.total;
		} catch (e) {
			if (mine !== seq) return;
			error = e;
		} finally {
			if (mine === seq) loading = false;
		}
	}

	// A new search term restarts from the first page; when already there, reload
	// directly (the offset effect below won't fire since offset didn't change).
	$effect(() => {
		search;
		untrack(() => {
			if (offset !== 0) offset = 0;
			else load();
		});
	});

	$effect(() => {
		offset;
		untrack(() => load());
	});

	function openCreate() {
		editing = null;
		modalOpen = true;
	}

	function openEdit(d: Device) {
		editing = d;
		modalOpen = true;
	}

	async function save(payload: DevicePayload) {
		saving = true;
		try {
			if (editing) await updateDevice(editing.id, payload);
			else await createDevice(payload);
			modalOpen = false;
			toast.success(editing ? 'Device updated' : 'Device created');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the device');
		} finally {
			saving = false;
		}
	}

	async function remove(d: Device) {
		const ok = await confirm({
			title: 'Delete device?',
			message: `"${d.name}" will be removed from inventory.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteDevice(d.id);
			toast.success('Device deleted');
			// Deleting the only row of a later page steps back one page; otherwise
			// reload in place so the count stays honest and the page backfills.
			if (items.length === 1 && offset > 0) offset = Math.max(0, offset - limit);
			else await load();
		} catch (e) {
			toast.fromError(e, "Couldn't delete the device");
		}
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Devices · Nashira</title></svelte:head>

<PageHeader title="Devices" description="Network device inventory.">
	{#snippet actions()}
		<RoleGate require="operator">
			<Button variant="primary" onclick={openCreate}><Plus size={15} />New device</Button>
		</RoleGate>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<div class="space-y-3">
		<Toolbar>
			{#snippet left()}
				<SearchInput bind:value={search} placeholder="Filter devices…" />
			{/snippet}
			{#snippet right()}
				<span class="text-xs text-surface-600-400">{total} device{total === 1 ? '' : 's'}</span>
			{/snippet}
		</Toolbar>

		<DataTable {loading} {columns} rows={items} rowKey={(d) => d.id} empty={search.trim() ? 'No devices match this search.' : 'No devices — add one by hand, or sync them from an inventory source.'}>
			{#snippet cell(row, col)}
				{#if col.key === 'name'}
					<span class="font-medium">{row.name}</span>
				{:else if col.key === 'ipAddress'}
					<code class="text-xs text-surface-600-400">{row.ipAddress}</code>
				{:else if col.key === 'site'}
					{row.site || '—'}
				{:else if col.key === 'role'}
					{row.role || '—'}
				{:else if col.key === 'status'}
					{#if row.status}<StatusBadge status={row.status} />{:else}<span
							class="text-surface-600-400">—</span
						>{/if}
				{:else if col.key === 'environments'}
					{@const envs = environmentsOf(row)}
					{#if envs.length === 0}
						<Badge tone="warning">parked</Badge>
					{:else}
						<div class="flex flex-wrap gap-1">
							{#each envs as env (env)}
								<Badge tone={env === 'production' ? 'error' : env === 'qa' ? 'warning' : 'neutral'}>
									{env}
								</Badge>
							{/each}
						</div>
					{/if}
				{:else if col.key === 'source'}
					{#if row.sourceId}
						<span
							class="text-xs text-surface-600-400"
							title={row.lastSyncAt
								? `Last sync ${new Date(row.lastSyncAt).toLocaleString()}`
								: 'Never synced'}
						>
							synced
						</span>
					{:else}
						<span class="text-xs text-surface-600-400">manual</span>
					{/if}
				{:else if col.key === 'actions'}
					<RoleGate require="operator">
						<div class="flex justify-end gap-1">
							<IconButton label="Edit device" onclick={() => openEdit(row)}>
								<Pencil size={14} />
							</IconButton>
							<IconButton label="Delete device" onclick={() => remove(row)}>
								<Trash2 size={14} />
							</IconButton>
						</div>
					</RoleGate>
				{/if}
			{/snippet}
		</DataTable>

		{#if total > limit}
			<Pagination {total} {limit} {offset} onchange={(o: number) => (offset = o)} />
		{/if}
	</div>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit device' : 'New device'}>
	<DeviceForm initial={editing} onsave={save} />
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button type="submit" form="device-form" variant="primary" loading={saving}>Save</Button>
	{/snippet}
</Modal>
