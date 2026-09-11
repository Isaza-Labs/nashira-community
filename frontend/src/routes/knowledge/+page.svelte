<script lang="ts">
	import { page } from '$app/state';
	import {
		listArticles,
		createArticle,
		updateArticle,
		deleteArticle,
		type Article,
		type ArticlePayload
	} from '$lib/api/knowledge.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		IconButton,
		Toolbar,
		SearchInput,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { timeAgo } from '$lib/utils/time';
	import ArticleForm from '$lib/components/knowledge/ArticleForm.svelte';
	import { Plus, Pencil, Trash2 } from 'lucide-svelte';

	let items = $state<Article[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);
	let search = $state('');

	let modalOpen = $state(false);
	let editing = $state<Article | null>(null);
	let saving = $state(false);

	const columns: Column[] = [
		{ key: 'title', header: 'Title' },
		{ key: 'tags', header: 'Tags' },
		{ key: 'updatedAt', header: 'Updated' },
		{ key: 'actions', header: '', align: 'right' }
	];

	const filtered = $derived(
		search.trim()
			? items.filter((a) =>
					`${a.title} ${a.tags.join(' ')}`.toLowerCase().includes(search.toLowerCase())
				)
			: items
	);

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listArticles(100, 0)).items;
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

	function openEdit(a: Article) {
		editing = a;
		modalOpen = true;
	}

	async function save(payload: ArticlePayload) {
		saving = true;
		try {
			if (editing) await updateArticle(editing.id, payload);
			else await createArticle(payload);
			modalOpen = false;
			toast.success(editing ? 'Article updated' : 'Article created');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the article');
		} finally {
			saving = false;
		}
	}

	async function remove(a: Article) {
		const ok = await confirm({
			title: 'Delete article?',
			message: `"${a.title}" will be removed.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteArticle(a.id);
			items = items.filter((x) => x.id !== a.id);
			toast.success('Article deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the article");
		}
	}

	function fmt(iso: string): string {
		return timeAgo(iso);
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Knowledge · Nashira</title></svelte:head>

<PageHeader title="Knowledge" description="Operational knowledge base.">
	{#snippet actions()}
		<RoleGate require="operator">
			<Button variant="primary" onclick={openCreate}><Plus size={15} />New article</Button>
		</RoleGate>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<div class="space-y-3">
		<Toolbar>
			{#snippet left()}
				<SearchInput bind:value={search} placeholder="Filter articles…" />
			{/snippet}
			{#snippet right()}
				<span class="text-xs text-surface-600-400">{filtered.length} of {items.length}</span>
			{/snippet}
		</Toolbar>

		<DataTable {loading} {columns} rows={filtered} rowKey={(a) => a.id} empty="Nothing written yet — the agent searches these while answering, so an article changes what chat knows.">
			{#snippet cell(row, col)}
				{#if col.key === 'title'}
					<span class="font-medium">{row.title}</span>
				{:else if col.key === 'tags'}
					{#if row.tags.length > 0}
						<div class="flex flex-wrap gap-1">
							{#each row.tags as t (t)}<Badge>{t}</Badge>{/each}
						</div>
					{:else}
						<span class="text-surface-600-400">—</span>
					{/if}
				{:else if col.key === 'updatedAt'}
					<span class="text-surface-600-400">{fmt(row.updatedAt)}</span>
				{:else if col.key === 'actions'}
					<RoleGate require="operator">
						<div class="flex justify-end gap-1">
							<IconButton label="Edit article" onclick={() => openEdit(row)}>
								<Pencil size={14} />
							</IconButton>
							<IconButton label="Delete article" onclick={() => remove(row)}>
								<Trash2 size={14} />
							</IconButton>
						</div>
					</RoleGate>
				{/if}
			{/snippet}
		</DataTable>
	</div>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit article' : 'New article'} size="lg">
	<ArticleForm initial={editing} onsave={save} />
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button type="submit" form="article-form" variant="primary" loading={saving}>Save</Button>
	{/snippet}
</Modal>
