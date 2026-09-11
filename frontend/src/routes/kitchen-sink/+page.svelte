<script lang="ts">
	// Dev showcase: exercises every design-system component on one page — the
	// acceptance surface for Slice 2. Reachable at /kitchen-sink (auth-guarded).
	import {
		PageHeader,
		Button,
		IconButton,
		Card,
		StatCard,
		EmptyState,
		ErrorState,
		Alert,
		Badge,
		StatusBadge,
		Kbd,
		Modal,
		DataTable,
		SortableTh,
		Toolbar,
		Pagination,
		SearchInput,
		Input,
		Textarea,
		Select,
		Checkbox,
		Tabs,
		Spinner,
		Skeleton,
		toast,
		confirm,
		type Column
	} from '$lib/components/ui';
	import { ApiError } from '$lib/api/client';
	import { Plus, Trash2 } from 'lucide-svelte';

	let modalOpen = $state(false);
	let tab = $state('overview');
	let search = $state('');
	let text = $state('');
	let area = $state('');
	let choice = $state('');
	let agree = $state(false);
	let offset = $state(0);
	let sortKey = $state<string | null>('name');
	let sortDir = $state<'asc' | 'desc'>('asc');

	type Row = { name: string; status: string; role: string };
	const rows: Row[] = [
		{ name: 'core-sw-01', status: 'active', role: 'Switch' },
		{ name: 'edge-fw-02', status: 'pending', role: 'Firewall' },
		{ name: 'dist-rt-03', status: 'failed', role: 'Router' }
	];
	const columns: Column[] = [
		{ key: 'name', header: 'Device' },
		{ key: 'status', header: 'Status' },
		{ key: 'role', header: 'Role' }
	];

	const roleOptions = [
		{ value: 'viewer', label: 'Viewer' },
		{ value: 'operator', label: 'Operator' },
		{ value: 'admin', label: 'Admin' }
	];
	const tabs = [
		{ value: 'overview', label: 'Overview' },
		{ value: 'details', label: 'Details' },
		{ value: 'activity', label: 'Activity' }
	];

	// ErrorState demands a thrown value; a synthetic ApiError shows the coded branch.
	const demoError = new ApiError({
		kind: 'server',
		status: 503,
		message: 'Service Unavailable',
		userMessage: 'The server had a problem processing your request. Please try again in a few minutes.'
	});

	const sortedRows = $derived(
		[...rows].sort((a, b) => {
			const k = (sortKey ?? 'name') as keyof Row;
			return String(a[k]).localeCompare(String(b[k])) * (sortDir === 'asc' ? 1 : -1);
		})
	);

	function toggleSort(col: string) {
		if (sortKey === col) {
			sortDir = sortDir === 'asc' ? 'desc' : 'asc';
		} else {
			sortKey = col;
			sortDir = 'asc';
		}
	}

	async function askDelete() {
		const ok = await confirm({
			title: 'Delete device?',
			message: 'This removes core-sw-01 from inventory. This action cannot be undone.',
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (ok) toast.success('Device deleted', { description: 'core-sw-01 removed from inventory.' });
		else toast.info('Cancelled');
	}
</script>

<svelte:head><title>Kitchen sink · Nashira</title></svelte:head>

<PageHeader
	title="Kitchen sink"
	description="Every design-system component, exercised on one page."
>
	{#snippet actions()}
		<Button variant="secondary" onclick={() => toast.info('Just a demo')}>Action</Button>
	{/snippet}
</PageHeader>

<div class="space-y-10">
	<section class="space-y-3">
		<h2 class="text-xs font-semibold uppercase tracking-wider text-surface-600-400">Buttons</h2>
		<div class="flex flex-wrap items-center gap-2">
			<Button variant="primary">Primary</Button>
			<Button variant="secondary">Secondary</Button>
			<Button variant="ghost">Ghost</Button>
			<Button variant="danger">Danger</Button>
			<Button variant="primary" loading>Loading</Button>
			<Button variant="primary" disabled>Disabled</Button>
		</div>
		<div class="flex flex-wrap items-center gap-2">
			<Button size="sm">Small</Button>
			<Button size="md">Medium</Button>
			<Button size="lg">Large</Button>
			<IconButton label="Add"><Plus size={16} /></IconButton>
			<IconButton label="Delete" variant="secondary"><Trash2 size={16} /></IconButton>
		</div>
	</section>

	<section class="space-y-3">
		<h2 class="text-xs font-semibold uppercase tracking-wider text-surface-600-400">Toasts</h2>
		<div class="flex flex-wrap gap-2">
			<Button size="sm" variant="secondary" onclick={() => toast.success('Saved', { description: 'Your changes were stored.' })}>Success</Button>
			<Button size="sm" variant="secondary" onclick={() => toast.error('Save failed', { description: 'The server rejected the request.' })}>Error</Button>
			<Button size="sm" variant="secondary" onclick={() => toast.warning('Heads up', { description: 'Your token expires in 5 minutes.' })}>Warning</Button>
			<Button size="sm" variant="secondary" onclick={() => toast.info('FYI', { description: 'Sync completed in the background.' })}>Info</Button>
			<Button size="sm" variant="secondary" onclick={() => toast.undoable('Item deleted', () => toast.info('Restored'))}>Undoable</Button>
			<Button
				size="sm"
				variant="secondary"
				onclick={() => toast.error('Upload failed', { description: 'Network unreachable.', action: { label: 'Retry', onClick: () => toast.success('Retried') } })}
			>Retry action</Button>
		</div>
	</section>

	<section class="space-y-3">
		<h2 class="text-xs font-semibold uppercase tracking-wider text-surface-600-400">Overlays</h2>
		<div class="flex flex-wrap gap-2">
			<Button variant="secondary" onclick={() => (modalOpen = true)}>Open modal</Button>
			<Button variant="danger" onclick={askDelete}><Trash2 size={15} />Delete with confirm</Button>
		</div>
		<Modal bind:open={modalOpen} title="Example modal">
			<p class="text-sm text-surface-700-300">
				Modal body content. Press Esc, click the backdrop, or use the buttons below to close.
			</p>
			{#snippet footer()}
				<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
				<Button
					variant="primary"
					onclick={() => {
						modalOpen = false;
						toast.success('Confirmed');
					}}>OK</Button
				>
			{/snippet}
		</Modal>
	</section>

	<section class="space-y-3">
		<h2 class="text-xs font-semibold uppercase tracking-wider text-surface-600-400">Inline alerts</h2>
		<div class="space-y-2">
			<Alert tone="primary" title="Info">Signal cyan-teal accent on cool graphite neutrals.</Alert>
			<Alert tone="success" title="Success">Build completed cleanly.</Alert>
			<Alert tone="warning" title="Warning">Your session expires soon.</Alert>
			<Alert tone="error" title="Error" dismissible ondismiss={() => toast.info('Dismissed')}>
				The last request could not be completed.
			</Alert>
		</div>
	</section>

	<section class="space-y-3">
		<h2 class="text-xs font-semibold uppercase tracking-wider text-surface-600-400">Cards & states</h2>
		<div class="grid gap-4 sm:grid-cols-3">
			<StatCard label="Devices" value={128} hint="+4 this week" />
			<StatCard label="Workflows" value={17} />
			<StatCard label="Open alerts" value={3} hint="2 critical" />
		</div>
		<div class="grid gap-4 sm:grid-cols-2">
			<Card title="Empty state">
				<EmptyState title="No devices yet" description="Add your first device to get started.">
					{#snippet actions()}
						<Button size="sm" variant="primary"><Plus size={15} />Add device</Button>
					{/snippet}
				</EmptyState>
			</Card>
			<Card title="Error state">
				<ErrorState error={demoError} onRetry={() => toast.info('Retrying…')} compact />
			</Card>
		</div>
	</section>

	<section class="space-y-3">
		<h2 class="text-xs font-semibold uppercase tracking-wider text-surface-600-400">Labels</h2>
		<div class="flex flex-wrap items-center gap-2">
			<Badge>neutral</Badge>
			<Badge tone="primary">primary</Badge>
			<Badge tone="success">success</Badge>
			<Badge tone="warning">warning</Badge>
			<Badge tone="error">error</Badge>
		</div>
		<div class="flex flex-wrap items-center gap-4">
			<StatusBadge status="active" />
			<StatusBadge status="pending" />
			<StatusBadge status="failed" />
			<StatusBadge status="draft" />
		</div>
		<div class="flex items-center gap-1 text-sm text-surface-600-400">
			Press <Kbd>Ctrl</Kbd><Kbd>K</Kbd> to search.
		</div>
	</section>

	<section class="space-y-3">
		<h2 class="text-xs font-semibold uppercase tracking-wider text-surface-600-400">Table</h2>
		<Toolbar>
			{#snippet left()}
				<SearchInput bind:value={search} placeholder="Filter devices…" />
			{/snippet}
			{#snippet right()}
				<Button size="sm" variant="secondary"><Plus size={15} />Add</Button>
			{/snippet}
		</Toolbar>
		<DataTable {columns} {rows}>
			{#snippet cell(row, col)}
				{#if col.key === 'status'}
					<StatusBadge status={row.status} />
				{:else if col.key === 'name'}
					<span class="font-medium">{row.name}</span>
				{:else}
					{row.role}
				{/if}
			{/snippet}
		</DataTable>
		<Pagination total={137} limit={25} {offset} onchange={(o) => (offset = o)} />
	</section>

	<section class="space-y-3">
		<h2 class="text-xs font-semibold uppercase tracking-wider text-surface-600-400">Sortable header</h2>
		<table class="w-full overflow-hidden rounded-lg border border-surface-200-800 text-sm">
			<thead class="bg-surface-100-900 text-surface-600-400">
				<tr>
					<SortableTh column="name" {sortKey} {sortDir} onSort={toggleSort}>Device</SortableTh>
					<SortableTh column="role" {sortKey} {sortDir} onSort={toggleSort}>Role</SortableTh>
				</tr>
			</thead>
			<tbody>
				{#each sortedRows as r (r.name)}
					<tr class="border-t border-surface-200-800">
						<td class="px-3 py-2">{r.name}</td>
						<td class="px-3 py-2">{r.role}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	</section>

	<section class="space-y-3">
		<h2 class="text-xs font-semibold uppercase tracking-wider text-surface-600-400">Forms</h2>
		<Tabs {tabs} bind:value={tab} />
		<div class="grid gap-4 pt-1 sm:grid-cols-2">
			<Input label="Device name" bind:value={text} placeholder="core-sw-01" hint="Unique hostname." />
			<Select label="Role" bind:value={choice} options={roleOptions} placeholder="Select a role…" />
			<Textarea label="Notes" bind:value={area} rows={3} placeholder="Optional notes…" />
			<div class="space-y-3">
				<Input label="With error" value="bad@" error="Enter a valid value." />
				<Checkbox bind:checked={agree} label="I understand this is a demo" />
			</div>
		</div>
		<p class="text-sm text-surface-600-400">
			Active tab: <span class="font-medium text-surface-800-200">{tab}</span>
		</p>
	</section>

	<section class="space-y-3">
		<h2 class="text-xs font-semibold uppercase tracking-wider text-surface-600-400">Feedback</h2>
		<div class="flex items-center gap-4">
			<Spinner size="sm" />
			<Spinner size="md" />
			<Spinner size="lg" />
		</div>
		<div class="max-w-sm space-y-2">
			<Skeleton class="h-4 w-3/4" />
			<Skeleton class="h-4 w-1/2" />
			<Skeleton class="h-20 w-full" />
		</div>
	</section>
</div>
