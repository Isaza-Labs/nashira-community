<script lang="ts">
	import { page } from '$app/state';
	import {
		listReports,
		createReport,
		deleteReport,
		downloadReport,
		type Report
	} from '$lib/api/reports.api';
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
		Checkbox,
		Alert,
		Spinner,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { Plus, Trash2, Download, FileText } from 'lucide-svelte';
	import SectionNav from '$lib/components/layout/SectionNav.svelte';

	let items = $state<Report[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);
	let includeExpired = $state(false);
	let downloadingId = $state<string | null>(null);

	// Bulk selection. Held as a plain array of ids rather than a Set so assigning it
	// is what triggers reactivity, and filtered against `items` on every read so a
	// report deleted individually cannot linger in the selection.
	let selectedIds = $state<string[]>([]);
	let bulkDeleting = $state(false);

	const selected = $derived(items.filter((r) => selectedIds.includes(r.id)));
	const allSelected = $derived(items.length > 0 && selected.length === items.length);

	function toggleOne(id: string, on: boolean) {
		selectedIds = on ? [...selectedIds, id] : selectedIds.filter((x) => x !== id);
	}

	function toggleAll(on: boolean) {
		selectedIds = on ? items.map((r) => r.id) : [];
	}

	let modalOpen = $state(false);
	let saving = $state(false);
	let formError = $state('');

	let fTitle = $state('');
	let fDescription = $state('');
	let fContent = $state('');
	let fContentType = $state('text/markdown');
	let fFileName = $state('');
	let fRetain = $state('30');

	const contentTypeOptions = [
		{ value: 'text/markdown', label: 'Markdown' },
		{ value: 'text/plain', label: 'Plain text' },
		{ value: 'text/csv', label: 'CSV' },
		{ value: 'application/json', label: 'JSON' },
		{ value: 'text/html', label: 'HTML' }
	];

	const columns: Column[] = [
		{ key: 'select', header: '', sortable: false, width: '2.25rem' },
		{ key: 'title', header: 'Report' },
		{ key: 'size', header: 'Size' },
		{ key: 'created', header: 'Created' },
		{ key: 'expires', header: 'Expires' },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	function formatSize(bytes: number): string {
		if (bytes < 1024) return `${bytes} B`;
		if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
		return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
	}

	function formatDate(iso: string): string {
		return new Date(iso).toLocaleString();
	}

	function isExpired(r: Report): boolean {
		return r.expiresAt !== null && new Date(r.expiresAt).getTime() <= Date.now();
	}

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listReports(includeExpired)).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	// Re-runs when the includeExpired toggle flips.
	$effect(() => {
		void includeExpired;
		selectedIds = [];
		load();
	});

	function openCreate() {
		formError = '';
		fTitle = '';
		fDescription = '';
		fContent = '';
		fContentType = 'text/markdown';
		fFileName = '';
		fRetain = '30';
		modalOpen = true;
	}

	async function save() {
		if (!fTitle.trim() || !fContent) {
			formError = 'Title and content are required.';
			return;
		}
		formError = '';
		saving = true;
		try {
			const retain = fRetain.trim() === '' ? null : Number.parseInt(fRetain, 10);
			if (retain !== null && (!Number.isFinite(retain) || retain < 1)) {
				formError = 'Retention must be a positive number of days, or blank to keep forever.';
				return;
			}
			await createReport({
				title: fTitle.trim(),
				description: fDescription,
				content: fContent,
				contentType: fContentType,
				fileName: fFileName.trim(),
				retainDays: retain
			});
			modalOpen = false;
			toast.success('Report stored');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to store the report');
		} finally {
			saving = false;
		}
	}

	async function remove(r: Report) {
		const ok = await confirm({
			title: 'Delete report?',
			message: `"${r.title}" will no longer be downloadable.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteReport(r.id);
			items = items.filter((x) => x.id !== r.id);
			selectedIds = selectedIds.filter((x) => x !== r.id);
			toast.success('Report deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the report");
		}
	}

	async function removeSelected() {
		const victims = selected;
		if (victims.length === 0) return;

		const ok = await confirm({
			title: `Delete ${victims.length} report${victims.length === 1 ? '' : 's'}?`,
			message: `The selected report${victims.length === 1 ? '' : 's'} will no longer be downloadable.`,
			tone: 'danger',
			confirmLabel: `Delete ${victims.length}`
		});
		if (!ok) return;

		bulkDeleting = true;
		// Deleted one by one; a failure is collected rather than aborting the rest.
		const failed: string[] = [];
		for (const r of victims) {
			try {
				await deleteReport(r.id);
				items = items.filter((x) => x.id !== r.id);
				selectedIds = selectedIds.filter((x) => x !== r.id);
			} catch {
				failed.push(r.title);
			}
		}
		bulkDeleting = false;

		if (failed.length === 0) {
			toast.success(`Deleted ${victims.length} report${victims.length === 1 ? '' : 's'}`);
		} else {
			toast.error(`Couldn't delete: ${failed.join(', ')}`);
		}
	}

	async function download(r: Report) {
		downloadingId = r.id;
		try {
			await downloadReport(r);
		} catch (e) {
			toast.fromError(e, 'Download failed');
		} finally {
			downloadingId = null;
		}
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Reports · Nashira</title></svelte:head>

<SectionNav id="artifacts" />

<PageHeader
	title="Reports"
	description="Artifacts produced by workflow runs, plus anything stored by hand."
>
	{#snippet actions()}
		<RoleGate require="operator">
			<Button variant="primary" onclick={openCreate}><Plus size={15} />Store report</Button>
		</RoleGate>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<div class="space-y-3">
		<div class="flex min-h-9 items-center justify-between gap-3">
			<RoleGate require="operator">
				<div class="flex items-center gap-3">
					<Checkbox
						bind:checked={() => allSelected, (on) => toggleAll(on)}
						label={allSelected ? 'Clear selection' : 'Select all'}
						disabled={bulkDeleting || items.length === 0}
					/>
					{#if selected.length > 0}
						<span class="text-sm text-surface-600-400">{selected.length} selected</span>
						<Button variant="danger" size="sm" loading={bulkDeleting} onclick={removeSelected}>
							<Trash2 size={14} />Delete selected
						</Button>
					{/if}
				</div>
			</RoleGate>
			<div class="ml-auto">
				<Checkbox bind:checked={includeExpired} label="Show expired" />
			</div>
		</div>

		<DataTable {loading} {columns} rows={items} rowKey={(r) => r.id} empty="No reports stored — workflow runs put evidence here, and so can you.">
			{#snippet cell(row, col)}
				{#if col.key === 'select'}
					<RoleGate require="operator">
						<Checkbox
							bind:checked={() => selectedIds.includes(row.id), (on) => toggleOne(row.id, on)}
							ariaLabel={`Select ${row.title}`}
							disabled={bulkDeleting}
						/>
					</RoleGate>
				{:else if col.key === 'title'}
					<div class="flex items-center gap-2">
						<FileText size={14} class="shrink-0 text-surface-600-400" />
						<div class="min-w-0">
							<div class="truncate font-medium">{row.title}</div>
							<div class="truncate text-xs text-surface-600-400">
								{row.fileName}
								{#if row.workflowRunId}· from a workflow run{/if}
							</div>
						</div>
					</div>
				{:else if col.key === 'size'}
					<span class="text-xs text-surface-600-400">{formatSize(row.sizeBytes)}</span>
				{:else if col.key === 'created'}
					<span class="text-xs text-surface-600-400">{formatDate(row.createdAt)}</span>
				{:else if col.key === 'expires'}
					{#if row.expiresAt === null}
						<span class="text-xs text-surface-600-400">never</span>
					{:else if isExpired(row)}
						<Badge tone="warning">expired</Badge>
					{:else}
						<span class="text-xs text-surface-600-400">{formatDate(row.expiresAt)}</span>
					{/if}
				{:else if col.key === 'actions'}
					<div class="flex justify-end gap-1">
						<IconButton
							label={`Download ${row.fileName}`}
							disabled={downloadingId === row.id}
							onclick={() => download(row)}
						>
							{#if downloadingId === row.id}<Spinner size="sm" />{:else}<Download size={14} />{/if}
						</IconButton>
						<RoleGate require="operator">
							<IconButton label="Delete report" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
						</RoleGate>
					</div>
				{/if}
			{/snippet}
		</DataTable>
	</div>
{/if}

<Modal bind:open={modalOpen} title="Store report" size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		<Input label="Title" bind:value={fTitle} required />
		<Textarea label="Description" bind:value={fDescription} rows={2} />
		<Textarea label="Content" bind:value={fContent} rows={10} spellcheck={false} required />

		<div class="grid gap-3 sm:grid-cols-3">
			<Select label="Content type" bind:value={fContentType} options={contentTypeOptions} />
			<Input label="File name" bind:value={fFileName} hint="Derived from the title if blank" />
			<Input
				label="Retention (days)"
				bind:value={fRetain}
				type="number"
				hint="Blank keeps it forever"
			/>
		</div>
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Store</Button>
	{/snippet}
</Modal>
