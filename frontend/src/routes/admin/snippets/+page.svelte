<script lang="ts">
	import { page } from '$app/state';
	import {
		listSnippets,
		deleteSnippet,
		duplicateSnippet,
		type Snippet
	} from '$lib/api/snippets.api';
	import { ApiError } from '$lib/api/client';
	import {
		PageHeader,
		Button,
		DataTable,
		Badge,
		IconButton,
		ErrorState,
		confirm,
		toast,
		type Column,
		type Tone
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import SnippetEditorModal from '$lib/components/snippet/SnippetEditorModal.svelte';
	import { Plus, Pencil, Copy, Trash2 } from 'lucide-svelte';

	let items = $state<Snippet[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	// The form itself lives in SnippetEditorModal, so the workflow view opens the
	// same editor instead of a copy of it. This page owns the list.
	let modalOpen = $state(false);
	let editing = $state<Snippet | null>(null);

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'type', header: 'Type' },
		{ key: 'targetMode', header: 'Targets' },
		{ key: 'idempotency', header: 'Idempotency' },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	function tierTone(tier: string): Tone {
		if (tier === 'idempotent') return 'success';
		if (tier === 'non_reversible') return 'error';
		return 'warning';
	}

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listSnippets()).items;
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

	function openEdit(s: Snippet) {
		editing = s;
		modalOpen = true;
	}

	// Duplicating is how someone starts from a body that already works: the editor
	// opens empty, so a seeded baseline — the paramiko SSH primitive above all —
	// is otherwise only reachable by retyping it.
	let duplicatingId = $state<string | null>(null);

	// Names are unique among active snippets and the API refuses a collision
	// outright, so the suffix is load-bearing rather than cosmetic.
	function copyName(base: string) {
		const taken = new Set(items.map((s) => s.name));
		let candidate = `${base} (copy)`;
		for (let n = 2; taken.has(candidate); n++) candidate = `${base} (copy ${n})`;
		return candidate;
	}

	async function duplicate(s: Snippet) {
		duplicatingId = s.id;
		const name = copyName(s.name);
		try {
			let copy: Snippet;
			try {
				copy = await duplicateSnippet(s.id, name);
			} catch (e) {
				// Turning network access ON is an admin act. An operator copying the
				// paramiko baseline should still get their copy — an inert one, said
				// out loud — rather than an error that reads as "duplicate is broken".
				if (s.networkEnabled && e instanceof ApiError && e.status === 403) {
					copy = await duplicateSnippet(s.id, name, false);
					toast.warning('Copied without network access', {
						description: 'Enabling it for interactive SSH needs an administrator.'
					});
				} else {
					throw e;
				}
			}
			await load();
			// Straight into the editor: a copy nobody renames is a second row with the
			// same job, which is the state this action exists to get out of.
			openEdit(copy);
		} catch (e) {
			toast.fromError(e, "Couldn't duplicate the snippet");
		} finally {
			duplicatingId = null;
		}
	}

	async function remove(s: Snippet) {
		const ok = await confirm({
			title: 'Delete snippet?',
			message: `"${s.name}" will be removed. The API refuses if a live workflow still references it.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteSnippet(s.id);
			items = items.filter((x) => x.id !== s.id);
			toast.success('Snippet deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the snippet");
		}
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Snippets · Nashira</title></svelte:head>

<PageHeader
	title="Snippets"
	description="The reusable steps a workflow node invokes by snippet_id."
>
	{#snippet actions()}
		<RoleGate require="operator">
			<Button variant="primary" onclick={openCreate}><Plus size={15} />New snippet</Button>
		</RoleGate>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<DataTable {loading} {columns} rows={items} rowKey={(s) => s.id} empty="No snippets yet — a workflow node runs a snippet, so this is where automation starts.">
		{#snippet cell(row, col)}
			{#if col.key === 'name'}
				<div class="font-medium">{row.name}</div>
				{#if row.description}
					<div class="max-w-[24rem] truncate text-xs text-surface-600-400">{row.description}</div>
				{/if}
			{:else if col.key === 'type'}
				<Badge>{row.type}</Badge>
			{:else if col.key === 'targetMode'}
				<span class="text-xs text-surface-600-400">{row.targetMode}</span>
			{:else if col.key === 'idempotency'}
				<div class="flex items-center gap-1.5">
					<Badge tone={tierTone(row.effectiveIdempotency)}>{row.effectiveIdempotency}</Badge>
					{#if row.idempotency && row.idempotency !== row.effectiveIdempotency}
						<span
							class="text-xs text-warning-600-400"
							title={`Declared '${row.idempotency}', but the handler's floor wins.`}
						>
							overruled
						</span>
					{/if}
				</div>
			{:else if col.key === 'actions'}
				<RoleGate require="operator">
					<div class="flex justify-end gap-1">
						<IconButton label="Edit snippet" onclick={() => openEdit(row)}><Pencil size={14} /></IconButton>
						<IconButton
							label="Duplicate snippet"
							disabled={duplicatingId !== null}
							onclick={() => duplicate(row)}
						>
							<Copy size={14} />
						</IconButton>
						<IconButton label="Delete snippet" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
					</div>
				</RoleGate>
			{/if}
		{/snippet}
	</DataTable>
{/if}

<SnippetEditorModal bind:open={modalOpen} snippet={editing} onsaved={load} />
