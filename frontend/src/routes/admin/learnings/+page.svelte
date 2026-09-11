<script lang="ts">
	import {
		listLearnings,
		createLearning,
		updateLearning,
		deleteLearning,
		type Learning
	} from '$lib/api/learnings.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		StatusBadge,
		IconButton,
		Input,
		Checkbox,
		Alert,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import { Plus, Pencil, Trash2 } from 'lucide-svelte';
	import SectionNav from '$lib/components/layout/SectionNav.svelte';

	let items = $state<Learning[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<Learning | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fPattern = $state('');
	let fCategory = $state('');
	let fService = $state('');
	let fTool = $state('');
	let fStrategy = $state('parameter_adjust');
	let fActive = $state(true);

	const columns: Column[] = [
		{ key: 'errorPattern', header: 'Error pattern' },
		{ key: 'toolName', header: 'Tool' },
		{ key: 'confidence', header: 'Confidence' },
		{ key: 'status', header: 'Status' },
		{ key: 'actions', header: '', align: 'right' }
	];

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listLearnings()).items;
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
		formError = '';
		fPattern = '';
		fCategory = '';
		fService = '';
		fTool = '';
		fStrategy = 'parameter_adjust';
		fActive = true;
		modalOpen = true;
	}

	function openEdit(l: Learning) {
		editing = l;
		formError = '';
		fPattern = l.errorPattern;
		fCategory = l.errorCategory;
		fService = l.serviceType;
		fTool = l.toolName;
		fStrategy = l.fixStrategy;
		fActive = l.isActive;
		modalOpen = true;
	}

	async function save() {
		if (!fPattern.trim()) {
			formError = 'Error pattern is required.';
			return;
		}
		saving = true;
		try {
			const payload = {
				errorPattern: fPattern.trim(),
				errorCategory: fCategory.trim(),
				serviceType: fService.trim(),
				toolName: fTool.trim(),
				fixStrategy: fStrategy.trim()
			};
			if (editing) await updateLearning(editing.id, { ...payload, isActive: fActive });
			else await createLearning(payload);
			modalOpen = false;
			toast.success(editing ? 'Learning updated' : 'Learning created');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the learning');
		} finally {
			saving = false;
		}
	}

	async function remove(l: Learning) {
		const ok = await confirm({
			title: 'Delete learning?',
			message: 'This error->fix hint will be removed.',
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteLearning(l.id);
			items = items.filter((x) => x.id !== l.id);
			toast.success('Learning deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the learning");
		}
	}
</script>

<svelte:head><title>Learnings · Nashira</title></svelte:head>

<SectionNav id="ai-studio" />

<PageHeader title="Learnings" description="Agent error-to-fix hints.">
	{#snippet actions()}
		<Button variant="primary" onclick={openCreate}><Plus size={15} />New learning</Button>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<DataTable {loading} {columns} rows={items} rowKey={(l) => l.id} empty="No learnings recorded — each one turns a failure the agent already hit into a fix it applies next time.">
		{#snippet cell(row, col)}
			{#if col.key === 'errorPattern'}
				<div class="flex items-center gap-2">
					<code class="max-w-[320px] truncate text-xs">{row.errorPattern}</code>
					{#if row.isSystem}<Badge>system</Badge>{/if}
				</div>
			{:else if col.key === 'toolName'}
				<span class="text-surface-600-400">{row.toolName || '—'}</span>
			{:else if col.key === 'confidence'}
				<span class="tabular-nums text-surface-600-400">{Math.round(row.confidence * 100)}%</span>
			{:else if col.key === 'status'}
				<StatusBadge status={row.isActive ? 'active' : 'disabled'} />
			{:else if col.key === 'actions'}
				{#if row.isSystem}
					<span class="text-xs text-surface-600-400">built-in</span>
				{:else}
					<div class="flex justify-end gap-1">
						<IconButton label="Edit learning" onclick={() => openEdit(row)}><Pencil size={14} /></IconButton>
						<IconButton label="Delete learning" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
					</div>
				{/if}
			{/if}
		{/snippet}
	</DataTable>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit learning' : 'New learning'} size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}
		<Input label="Error pattern" bind:value={fPattern} required hint="Substring or regex to match" />
		<div class="grid gap-3 sm:grid-cols-2">
			<Input label="Error category" bind:value={fCategory} />
			<Input label="Service type" bind:value={fService} />
		</div>
		<div class="grid gap-3 sm:grid-cols-2">
			<Input label="Tool name" bind:value={fTool} />
			<Input label="Fix strategy" bind:value={fStrategy} hint="e.g. parameter_adjust" />
		</div>
		{#if editing}<Checkbox bind:checked={fActive} label="Active" />{/if}
	</div>
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>
