<script lang="ts">
	import {
		listSources,
		createSource,
		updateSource,
		deleteSource,
		syncSource,
		type InventorySource,
		type SourcePayload
	} from '$lib/api/inventory.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		IconButton,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { timeAgo } from '$lib/utils/time';
	import SourceForm from '$lib/components/inventory/SourceForm.svelte';
	import { Plus, Pencil, Trash2, RefreshCw } from 'lucide-svelte';

	let items = $state<InventorySource[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<InventorySource | null>(null);
	let saving = $state(false);
	let busyId = $state<string | null>(null);

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'baseUrl', header: 'Base URL' },
		{ key: 'lastSyncedAt', header: 'Last synced' },
		{ key: 'actions', header: '', align: 'right' }
	];

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listSources(100, 0)).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	function openCreate() {
		editing = null;
		modalOpen = true;
	}

	function openEdit(s: InventorySource) {
		editing = s;
		modalOpen = true;
	}

	async function save(payload: SourcePayload) {
		saving = true;
		try {
			if (editing) await updateSource(editing.id, payload);
			else await createSource(payload);
			modalOpen = false;
			toast.success(editing ? 'Source updated' : 'Source created');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the source');
		} finally {
			saving = false;
		}
	}

	async function remove(s: InventorySource) {
		const ok = await confirm({
			title: 'Delete source?',
			message: `"${s.name}" will be removed. Devices it imported are not deleted.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteSource(s.id);
			items = items.filter((x) => x.id !== s.id);
			toast.success('Source deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the source");
		}
	}

	async function runSync(s: InventorySource, dryRun: boolean) {
		if (!dryRun) {
			const ok = await confirm({
				title: `Sync "${s.name}"?`,
				message: 'This imports devices from NetBox into the inventory.',
				confirmLabel: 'Sync'
			});
			if (!ok) return;
		}
		busyId = s.id;
		try {
			const r = await syncSource(s.id, dryRun);
			const desc = `${r.created} created, ${r.updated} updated, ${r.unchanged} unchanged (${r.total} total)`;
			if (dryRun) {
				toast.info('Dry run complete', { description: desc });
			} else {
				toast.success('Sync complete', { description: desc });
				await load();
			}
		} catch (e) {
			toast.fromError(e, 'Sync failed');
		} finally {
			busyId = null;
		}
	}

	function fmt(iso: string | null): string {
		return timeAgo(iso);
	}
</script>

<svelte:head><title>Inventory · Nashira</title></svelte:head>

<PageHeader title="Inventory" description="NetBox inventory sources and device sync.">
	{#snippet actions()}
		<RoleGate require="admin">
			<Button variant="primary" onclick={openCreate}><Plus size={15} />New source</Button>
		</RoleGate>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<DataTable {loading} {columns} rows={items} rowKey={(s) => s.id} empty="No inventory sources yet.">
		{#snippet cell(row, col)}
			{#if col.key === 'name'}
				<span class="font-medium">{row.name}</span>
			{:else if col.key === 'baseUrl'}
				<code class="text-xs text-surface-600-400">{row.baseUrl}</code>
			{:else if col.key === 'lastSyncedAt'}
				<span class="text-surface-600-400">{fmt(row.lastSyncedAt)}</span>
			{:else if col.key === 'actions'}
				<div class="flex items-center justify-end gap-1">
					<RoleGate require="operator">
						<Button
							size="sm"
							variant="ghost"
							disabled={busyId === row.id}
							onclick={() => runSync(row, true)}
						>
							Dry run
						</Button>
						<Button
							size="sm"
							variant="secondary"
							loading={busyId === row.id}
							onclick={() => runSync(row, false)}
						>
							<RefreshCw size={13} />Sync
						</Button>
					</RoleGate>
					<RoleGate require="admin">
						<IconButton label="Edit source" onclick={() => openEdit(row)}>
							<Pencil size={14} />
						</IconButton>
						<IconButton label="Delete source" onclick={() => remove(row)}>
							<Trash2 size={14} />
						</IconButton>
					</RoleGate>
				</div>
			{/if}
		{/snippet}
	</DataTable>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit source' : 'New source'}>
	<SourceForm initial={editing} onsave={save} />
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button type="submit" form="source-form" variant="primary" loading={saving}>Save</Button>
	{/snippet}
</Modal>
