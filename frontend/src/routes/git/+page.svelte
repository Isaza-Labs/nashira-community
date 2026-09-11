<script lang="ts">
	import {
		listRepos,
		createRepo,
		updateRepo,
		deleteRepo,
		type GitRepo,
		type RepoPayload
	} from '$lib/api/git.api';
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
	import RepoForm from '$lib/components/git/RepoForm.svelte';
	import { Plus, Pencil, Trash2 } from 'lucide-svelte';

	let items = $state<GitRepo[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<GitRepo | null>(null);
	let saving = $state(false);

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'url', header: 'URL' },
		{ key: 'defaultBranch', header: 'Branch' },
		{ key: 'actions', header: '', align: 'right' }
	];

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listRepos(100, 0)).items;
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

	function openEdit(r: GitRepo) {
		editing = r;
		modalOpen = true;
	}

	async function save(payload: RepoPayload) {
		saving = true;
		try {
			if (editing) await updateRepo(editing.id, payload);
			else await createRepo(payload);
			modalOpen = false;
			toast.success(editing ? 'Repository updated' : 'Repository created');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the repository');
		} finally {
			saving = false;
		}
	}

	async function remove(r: GitRepo) {
		const ok = await confirm({
			title: 'Delete repository?',
			message: `"${r.name}" will be removed. The remote is not affected.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteRepo(r.id);
			items = items.filter((x) => x.id !== r.id);
			toast.success('Repository deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the repository");
		}
	}
</script>

<svelte:head><title>Git · Nashira</title></svelte:head>

<PageHeader title="Git" description="Managed repositories.">
	{#snippet actions()}
		<RoleGate require="admin">
			<Button variant="primary" onclick={openCreate}><Plus size={15} />New repository</Button>
		</RoleGate>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<DataTable {loading} {columns} rows={items} rowKey={(r) => r.id} empty="No repositories yet.">
		{#snippet cell(row, col)}
			{#if col.key === 'name'}
				<a href={`/git/${row.id}`} class="font-medium text-primary-700-300 hover:underline">
					{row.name}
				</a>
			{:else if col.key === 'url'}
				<code class="text-xs text-surface-600-400">{row.url}</code>
			{:else if col.key === 'defaultBranch'}
				{row.defaultBranch}
			{:else if col.key === 'actions'}
				<RoleGate require="admin">
					<div class="flex justify-end gap-1">
						<IconButton label="Edit repository" onclick={() => openEdit(row)}>
							<Pencil size={14} />
						</IconButton>
						<IconButton label="Delete repository" onclick={() => remove(row)}>
							<Trash2 size={14} />
						</IconButton>
					</div>
				</RoleGate>
			{/if}
		{/snippet}
	</DataTable>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit repository' : 'New repository'}>
	<RepoForm initial={editing} onsave={save} />
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button type="submit" form="repo-form" variant="primary" loading={saving}>Save</Button>
	{/snippet}
</Modal>
