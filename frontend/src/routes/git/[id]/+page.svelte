<script lang="ts">
	import { page } from '$app/state';
	import {
		getRepo,
		getBranches,
		pull,
		push,
		checkout,
		getDiff,
		commit,
		type GitRepo,
		type Branches,
		type GitDiff
	} from '$lib/api/git.api';
	import {
		PageHeader,
		Tabs,
		Select,
		Button,
		Modal,
		Input,
		Checkbox,
		Spinner,
		ErrorState,
		toast
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import FileBrowser from '$lib/components/git/FileBrowser.svelte';
	import DiffView from '$lib/components/git/DiffView.svelte';
	import WebhookPanel from '$lib/components/git/WebhookPanel.svelte';
	import { ArrowLeft, GitBranch, RefreshCw, Upload, Check } from 'lucide-svelte';

	const id = $derived(page.params.id!);

	let repo = $state<GitRepo | null>(null);
	let branches = $state<Branches | null>(null);
	let loading = $state(true);
	let error = $state<unknown>(null);
	let currentRef = $state('');
	let browserKey = $state(0);
	let busy = $state(false);

	let tab = $state('files');
	const tabs = [
		{ value: 'files', label: 'Files' },
		{ value: 'changes', label: 'Changes' },
		{ value: 'webhooks', label: 'Webhooks' }
	];

	let diff = $state<GitDiff | null>(null);
	let diffLoading = $state(false);
	let diffError = $state<unknown>(null);

	let commitOpen = $state(false);
	let commitMsg = $state('');
	let commitPush = $state(false);
	let committing = $state(false);

	async function loadCore() {
		loading = true;
		error = null;
		try {
			const [r, b] = await Promise.all([getRepo(id), getBranches(id).catch(() => null)]);
			repo = r;
			branches = b;
			currentRef = b?.current || r.defaultBranch;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		id;
		loadCore();
	});

	async function loadDiff() {
		diffLoading = true;
		diffError = null;
		try {
			diff = await getDiff(id);
		} catch (e) {
			diffError = e;
		} finally {
			diffLoading = false;
		}
	}

	$effect(() => {
		if (tab === 'changes') loadDiff();
	});

	async function op(kind: 'pull' | 'push' | 'checkout') {
		busy = true;
		try {
			const res =
				kind === 'pull'
					? await pull(id, currentRef)
					: kind === 'push'
						? await push(id, currentRef)
						: await checkout(id, currentRef);
			if (res.ok) toast.success(res.message || `${kind} succeeded`);
			else toast.error(res.message || `${kind} failed`);
			if (kind !== 'push') {
				browserKey++; // refresh the file listing (new commits / branch)
				branches = await getBranches(id).catch(() => branches);
			}
		} catch (e) {
			toast.fromError(e, `${kind} failed`);
		} finally {
			busy = false;
		}
	}

	async function doCommit() {
		if (!commitMsg.trim()) return;
		committing = true;
		try {
			const res = await commit(id, { message: commitMsg.trim(), push: commitPush });
			if (res.ok) {
				const sha = res.commitSha ? ` (${res.commitSha.slice(0, 7)})` : '';
				toast.success((res.message || 'Committed') + sha);
				commitOpen = false;
				commitMsg = '';
				commitPush = false;
				await loadDiff();
			} else {
				toast.error(res.message || 'Commit failed');
			}
		} catch (e) {
			toast.fromError(e, 'Commit failed');
		} finally {
			committing = false;
		}
	}

	const branchOptions = $derived((branches?.branches ?? []).map((b) => ({ value: b, label: b })));
</script>

<svelte:head><title>{repo?.name ?? 'Repository'} · Nashira</title></svelte:head>

<a
	href="/git"
	class="mb-3 inline-flex items-center gap-1.5 text-sm text-surface-600-400 hover:text-surface-950-50"
>
	<ArrowLeft size={15} />Git
</a>

{#if loading}
	<div class="flex justify-center py-16"><Spinner size="lg" /></div>
{:else if error}
	<ErrorState {error} onRetry={loadCore} />
{:else if repo}
	<PageHeader title={repo.name} description={repo.url}>
		{#snippet actions()}
			{#if branchOptions.length > 0}
				<div class="w-40"><Select bind:value={currentRef} options={branchOptions} /></div>
			{:else}
				<span class="inline-flex items-center gap-1.5 text-sm text-surface-600-400">
					<GitBranch size={14} />{currentRef}
				</span>
			{/if}
			<RoleGate require="operator">
				<Button size="sm" variant="ghost" loading={busy} onclick={() => op('pull')}>
					<RefreshCw size={14} />Pull
				</Button>
				<Button size="sm" variant="ghost" loading={busy} onclick={() => op('checkout')}>
					<GitBranch size={14} />Checkout
				</Button>
				<Button size="sm" variant="ghost" loading={busy} onclick={() => op('push')}>
					<Upload size={14} />Push
				</Button>
			</RoleGate>
		{/snippet}
	</PageHeader>

	<Tabs {tabs} bind:value={tab} />

	<div class="pt-4">
		{#if tab === 'files'}
			{#key `${currentRef}:${browserKey}`}
				<FileBrowser repoId={id} gitRef={currentRef} />
			{/key}
		{:else if tab === 'webhooks'}
			<WebhookPanel repoId={id} defaultBranch={repo.defaultBranch} />
		{:else}
			<div class="space-y-3">
				<div class="flex justify-end">
					<RoleGate require="operator">
						<Button variant="primary" onclick={() => (commitOpen = true)}>
							<Check size={15} />Commit changes
						</Button>
					</RoleGate>
				</div>
				{#if diffLoading}
					<div class="flex justify-center py-10"><Spinner /></div>
				{:else if diffError}
					<ErrorState error={diffError} onRetry={loadDiff} compact />
				{:else if diff}
					<DiffView patch={diff.patch} />
				{/if}
			</div>
		{/if}
	</div>
{/if}

<Modal bind:open={commitOpen} title="Commit changes">
	<div class="space-y-3">
		<Input label="Commit message" bind:value={commitMsg} required />
		<Checkbox bind:checked={commitPush} label="Push after commit" />
	</div>
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (commitOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={committing} disabled={!commitMsg.trim()} onclick={doCommit}>
			Commit
		</Button>
	{/snippet}
</Modal>
