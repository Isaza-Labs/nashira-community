<script lang="ts">
	// Repository file browser at a fixed ref. The parent re-keys this component when
	// the ref changes, so `gitRef` is constant for its lifetime and only `path`
	// navigation reloads. Blobs open in a viewer modal; Operators can edit + commit.
	import { listFiles, readFile, writeFile, type GitFile, type FileContent } from '$lib/api/git.api';
	import { Modal, Spinner, ErrorState, Button, Textarea, Input, Checkbox, toast } from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { Folder, File as FileIcon, ChevronRight, Pencil } from 'lucide-svelte';

	let { repoId, gitRef }: { repoId: string; gitRef: string } = $props();

	let path = $state('');
	let entries = $state<GitFile[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let fileOpen = $state(false);
	let file = $state<FileContent | null>(null);
	let fileLoading = $state(false);
	let fileError = $state<unknown>(null);

	let editing = $state(false);
	let draft = $state('');
	let commitMsg = $state('');
	let push = $state(false);
	let saving = $state(false);

	async function loadEntries() {
		loading = true;
		error = null;
		try {
			const r = await listFiles(repoId, path, gitRef);
			entries = r.entries
				.slice()
				.sort((a, b) =>
					a.type === b.type ? a.path.localeCompare(b.path) : a.type === 'tree' ? -1 : 1
				);
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		path;
		loadEntries();
	});

	function baseName(p: string): string {
		const i = p.lastIndexOf('/');
		return i >= 0 ? p.slice(i + 1) : p;
	}

	const crumbs = $derived(path ? path.split('/').filter(Boolean) : []);

	function open(entry: GitFile) {
		if (entry.type === 'tree') {
			path = entry.path;
			return;
		}
		fileOpen = true;
		file = null;
		fileError = null;
		editing = false;
		fileLoading = true;
		readFile(repoId, entry.path, gitRef)
			.then((f) => (file = f))
			.catch((e) => (fileError = e))
			.finally(() => (fileLoading = false));
	}

	function startEdit() {
		if (!file) return;
		draft = file.content;
		commitMsg = `Update ${file.path}`;
		push = false;
		editing = true;
	}

	async function save() {
		if (!file || !commitMsg.trim()) return;
		saving = true;
		try {
			const res = await writeFile(repoId, {
				path: file.path,
				content: draft,
				commitMessage: commitMsg.trim(),
				branch: gitRef,
				push
			});
			if (res.ok) {
				toast.success(res.message || 'File committed', {
					description: res.commitSha ? res.commitSha.slice(0, 7) : undefined
				});
				editing = false;
				file = await readFile(repoId, file.path, gitRef);
			} else {
				toast.error(res.message || 'Save failed');
			}
		} catch (e) {
			toast.fromError(e, 'Failed to save the file');
		} finally {
			saving = false;
		}
	}
</script>

<div class="space-y-2">
	<div class="flex flex-wrap items-center gap-1 text-sm text-surface-600-400">
		<button type="button" class="hover:text-surface-950-50" onclick={() => (path = '')}>{gitRef}</button>
		{#each crumbs as c, i (i)}
			<ChevronRight size={12} class="text-surface-600-400" />
			<button
				type="button"
				class="hover:text-surface-950-50"
				onclick={() => (path = crumbs.slice(0, i + 1).join('/'))}
			>
				{c}
			</button>
		{/each}
	</div>

	{#if loading}
		<div class="flex justify-center py-10"><Spinner /></div>
	{:else if error}
		<ErrorState {error} onRetry={loadEntries} compact />
	{:else if entries.length === 0}
		<p class="rounded-xl border border-surface-200-800 px-4 py-8 text-center text-sm text-surface-600-400">
			Empty directory.
		</p>
	{:else}
		<div class="overflow-hidden rounded-xl border border-surface-200-800">
			{#each entries as e (e.path)}
				<button
					type="button"
					onclick={() => open(e)}
					class="flex w-full items-center gap-2 border-b border-surface-100-900 px-4 py-2 text-left text-sm transition last:border-0 hover:bg-surface-100-900/60"
				>
					{#if e.type === 'tree'}
						<Folder size={15} class="shrink-0 text-primary-600-400" />
					{:else}
						<FileIcon size={15} class="shrink-0 text-surface-600-400" />
					{/if}
					<span class="flex-1 truncate">{baseName(e.path)}</span>
					{#if e.type === 'blob'}
						<span class="text-xs tabular-nums text-surface-600-400">{e.size} B</span>
					{/if}
				</button>
			{/each}
		</div>
	{/if}
</div>

<Modal bind:open={fileOpen} title={file?.path ?? 'File'} size="lg">
	{#if fileLoading}
		<div class="flex justify-center py-10"><Spinner /></div>
	{:else if fileError}
		<ErrorState error={fileError} compact />
	{:else if file}
		{#if editing}
			<div class="space-y-3">
				<Textarea bind:value={draft} rows={16} />
				<div class="grid gap-3 sm:grid-cols-[1fr_auto] sm:items-center">
					<Input label="Commit message" bind:value={commitMsg} required />
					<div class="pt-5"><Checkbox bind:checked={push} label="Push after commit" /></div>
				</div>
			</div>
		{:else if file.isBinary}
			<p class="text-sm text-surface-600-400">
				Binary file ({file.size} bytes) — preview not available.
			</p>
		{:else}
			<pre
				class="max-h-[60vh] overflow-auto rounded-lg bg-surface-100-900 p-3 text-xs leading-relaxed"><code
					>{file.content}</code
				></pre>
		{/if}
	{/if}
	{#snippet footer()}
		{#if editing}
			<Button variant="ghost" onclick={() => (editing = false)}>Cancel</Button>
			<Button variant="primary" loading={saving} disabled={!commitMsg.trim()} onclick={save}>
				Commit
			</Button>
		{:else}
			<Button variant="ghost" onclick={() => (fileOpen = false)}>Close</Button>
			{#if file && !file.isBinary}
				<RoleGate require="operator">
					<Button variant="secondary" onclick={startEdit}><Pencil size={14} />Edit</Button>
				</RoleGate>
			{/if}
		{/if}
	{/snippet}
</Modal>
