<script lang="ts">
	import { page } from '$app/state';
	import {
		listVendorCommands,
		listVendorPlatforms,
		createVendorCommand,
		updateVendorCommand,
		deleteVendorCommand,
		type VendorCommand,
		type VendorCommandPayload,
		type VendorPlatform
	} from '$lib/api/vendor-commands.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		IconButton,
		Input,
		Select,
		Textarea,
		Checkbox,
		Alert,
		SearchInput,
		Toolbar,
		ErrorState,
		FieldHint,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { Plus, Pencil, Trash2, RefreshCw, Layers } from 'lucide-svelte';

	let items = $state<VendorCommand[]>([]);
	let total = $state(0);
	let loading = $state(true);
	let error = $state<unknown>(null);
	let search = $state('');

	// Server-side: the catalogue is one row per intent per platform, so a dozen
	// vendors outruns any page size. Client-side search then narrows what came
	// back — the two compose.
	let platform = $state('');

	// Derived server-side from the catalogue and the inventory, so it cannot go
	// stale against a vendor YAML added since this page was built.
	let platforms = $state<VendorPlatform[]>([]);

	// A platform the admin just named that has no command yet. It is not a row
	// anywhere until one is saved against it — `platform` is a column on
	// vendor_commands, not an entity — so it lives here for the length of the
	// visit rather than being written to the browser, where it would be a ghost
	// only this person can see.
	let pendingPlatform = $state('');

	const platformOptions = $derived([
		{ value: '', label: `All platforms (${total})` },
		...platforms.map((p) => ({
			value: p.platform,
			label: p.commandCount
				? `${p.platform} — ${p.commandCount} command${p.commandCount === 1 ? '' : 's'}`
				: `${p.platform} — no commands yet`
		})),
		...(pendingPlatform && !platforms.some((p) => p.platform === pendingPlatform)
			? [{ value: pendingPlatform, label: `${pendingPlatform} — no commands yet` }]
			: [])
	]);

	// What the form offers: every known platform plus the one being introduced.
	const knownPlatforms = $derived(
		[...new Set([...platforms.map((p) => p.platform), pendingPlatform].filter(Boolean))].sort()
	);

	// Devices running a platform the catalogue says nothing about. Every intent
	// resolves to a 404 on those, which surfaces as a workflow that works on
	// every vendor but one.
	const uncatalogued = $derived(platforms.filter((p) => p.deviceCount > 0 && p.commandCount === 0));

	let addTypeOpen = $state(false);
	let newPlatform = $state('');
	let addTypeError = $state('');

	let modalOpen = $state(false);
	let editing = $state<VendorCommand | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fIntent = $state('');
	let fPlatform = $state('');
	let fCommand = $state('');
	let fDescription = $state('');
	let fReadOnly = $state(true);
	let fParser = $state('');

	const columns: Column[] = [
		{ key: 'intent', header: 'Intent' },
		{ key: 'platform', header: 'Platform' },
		{ key: 'command', header: 'Command' },
		{ key: 'readOnly', header: 'Read-only' },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	const filtered = $derived(
		search.trim()
			? items.filter((c) =>
					`${c.intent} ${c.platform} ${c.command}`.toLowerCase().includes(search.toLowerCase())
				)
			: items
	);

	async function load() {
		loading = true;
		error = null;
		try {
			const res = await listVendorCommands({ platform });
			items = res.items;
			total = res.total;
			// Fanned out rather than awaited in sequence: the table is what the page
			// is for, and a slow platform roll-up should not hold it back. A failure
			// there degrades to the platforms already loaded — the filter keeps
			// working, it just stops learning about new ones.
			void listVendorPlatforms()
				.then((p) => (platforms = p))
				.catch(() => {});
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	// Refetches on filter change too — `platform` is read here, so the effect
	// re-runs when it does.
	$effect(() => {
		void platform;
		load();
	});

	function openAddType() {
		newPlatform = '';
		addTypeError = '';
		addTypeOpen = true;
	}

	// A platform becomes real when the first command is catalogued against it, so
	// this hands straight off to the create form rather than pretending to have
	// stored something.
	function confirmAddType() {
		const name = newPlatform.trim().toLowerCase();
		if (!name) {
			addTypeError = 'Enter a platform identifier.';
			return;
		}
		// The same shape the backend lowercases writes into, and the same one
		// Netmiko uses for device_type. Rejecting `cisco-ios` here is the point:
		// resolve matches the string exactly, so a near-miss is a 404 at run time
		// and nothing before it.
		if (!/^[a-z0-9_]+$/.test(name)) {
			addTypeError = 'Lowercase letters, digits and underscores only — e.g. paloalto_panos.';
			return;
		}
		if (platforms.some((p) => p.platform === name && p.commandCount > 0)) {
			addTypeError = `${name} is already catalogued. Filter by it instead.`;
			return;
		}
		pendingPlatform = name;
		addTypeOpen = false;
		openCreate();
		fPlatform = name;
	}

	function openCreate() {
		editing = null;
		formError = '';
		fIntent = '';
		fPlatform = '';
		fCommand = '';
		fDescription = '';
		fReadOnly = true;
		fParser = '';
		modalOpen = true;
	}

	function openEdit(c: VendorCommand) {
		editing = c;
		formError = '';
		fIntent = c.intent;
		fPlatform = c.platform;
		fCommand = c.command;
		fDescription = c.description ?? '';
		fReadOnly = c.readOnly;
		fParser = c.parserTemplate ?? '';
		modalOpen = true;
	}

	function payload(): VendorCommandPayload {
		return {
			intent: fIntent.trim(),
			platform: fPlatform.trim(),
			command: fCommand.trim(),
			description: fDescription,
			readOnly: fReadOnly,
			parserTemplate: fParser
		};
	}

	async function save() {
		if (!editing && (!fIntent.trim() || !fPlatform.trim())) {
			formError = 'Intent and platform are both required.';
			return;
		}
		if (!fCommand.trim()) {
			formError = 'Command is required.';
			return;
		}
		formError = '';
		saving = true;
		try {
			if (editing) await updateVendorCommand(editing.id, payload());
			else await createVendorCommand(payload());
			modalOpen = false;
			toast.success(editing ? 'Command updated' : 'Command created');
			// The platform is a real one now: the reload picks it up from the
			// server, so the session-scoped placeholder has nothing left to do.
			if (pendingPlatform === payload().platform) pendingPlatform = '';
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the command');
		} finally {
			saving = false;
		}
	}

	async function remove(c: VendorCommand) {
		const ok = await confirm({
			title: 'Delete command?',
			message: `"${c.intent}" on "${c.platform}" will be removed. Workflows resolving that intent on this platform will start failing.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteVendorCommand(c.id);
			items = items.filter((x) => x.id !== c.id);
			toast.success('Command deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the command");
		}
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Vendor commands · Nashira</title></svelte:head>

<PageHeader
	title="Vendor commands"
	description="Intent plus platform to CLI command — what makes one workflow multi-vendor."
>
	{#snippet actions()}
		<Button variant="ghost" onclick={load}><RefreshCw size={15} />Refresh</Button>
		<RoleGate require="operator">
			<Button variant="ghost" onclick={openAddType}><Layers size={15} />Add platform</Button>
			<Button variant="primary" onclick={openCreate}><Plus size={15} />New command</Button>
		</RoleGate>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<div class="space-y-3">
		{#if uncatalogued.length > 0}
			<Alert tone="warning">
				{uncatalogued.length === 1 ? 'One platform is' : `${uncatalogued.length} platforms are`} in the
				inventory with nothing catalogued —
				{#each uncatalogued as p, i (p.platform)}<code class="text-xs">{p.platform}</code>{i <
					uncatalogued.length - 1
						? ', '
						: ''}{/each}. Every intent resolves to a 404 on those devices, so a workflow will run
				everywhere except there.
			</Alert>
		{/if}

		<Toolbar>
			{#snippet left()}
				<div class="w-56">
					<Select bind:value={platform} options={platformOptions} />
				</div>
				<SearchInput bind:value={search} placeholder="Filter by intent, platform or command…" />
			{/snippet}
			{#snippet right()}
				<span class="text-xs text-surface-600-400">
					{#if search.trim()}
						{filtered.length} of {items.length} loaded
					{:else}
						{items.length}{items.length === total ? '' : ` of ${total}`}
						{platform ? `on ${platform}` : 'catalogued'}
					{/if}
				</span>
			{/snippet}
		</Toolbar>

		<DataTable
			{loading}
			{columns}
			rows={filtered}
			rowKey={(c) => c.id}
			empty={search.trim()
				? `Nothing in the ${items.length} loaded ${items.length === 1 ? 'entry' : 'entries'} matches “${search.trim()}”.`
				: platform
					? `Nothing catalogued for ${platform} yet — add a command for it, or clear the filter.`
					: 'Nothing catalogued yet — map an intent like bgp_summary to each platform’s CLI and stop branching inside steps.'}
		>
			{#snippet cell(row, col)}
				{#if col.key === 'intent'}
					<span class="font-medium">{row.intent}</span>
					{#if row.description}
						<div class="max-w-[20rem] truncate text-xs text-surface-600-400">{row.description}</div>
					{/if}
				{:else if col.key === 'platform'}
					<Badge>{row.platform}</Badge>
				{:else if col.key === 'command'}
					<code class="text-xs text-surface-600-400">{row.command}</code>
				{:else if col.key === 'readOnly'}
					<Badge tone={row.readOnly ? 'success' : 'warning'}>{row.readOnly ? 'read' : 'writes'}</Badge>
				{:else if col.key === 'actions'}
					<RoleGate require="operator">
						<div class="flex justify-end gap-1">
							<IconButton label="Edit command" onclick={() => openEdit(row)}><Pencil size={14} /></IconButton>
							<IconButton label="Delete command" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
						</div>
					</RoleGate>
				{/if}
			{/snippet}
		</DataTable>
	</div>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit command' : 'New command'}>
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		<div class="grid gap-3 sm:grid-cols-2">
			<Input
				label="Intent"
				bind:value={fIntent}
				disabled={!!editing}
				required={!editing}
				hint="Vendor-neutral, e.g. show_interfaces"
			/>
			{#if editing}
				<Input label="Platform" bind:value={fPlatform} disabled hint="Netmiko device_type" />
			{:else}
				<div class="space-y-1">
					<span class="inline-flex items-center gap-1 text-sm font-medium">
						Platform <span class="text-error-500">*</span>
						<FieldHint
							label="Platform"
							help="The Netmiko device_type the SSH runner connects with — cisco_ios, juniper_junos, nokia_srl."
							detail="Picked from a list rather than typed because resolve matches this string exactly: a workflow asking for an intent on cisco-ios finds nothing that cisco_ios has. Use Add platform for a vendor that is not here yet."
						/>
					</span>
					<Select
						bind:value={fPlatform}
						options={knownPlatforms.map((p) => ({ value: p, label: p }))}
						placeholder="Select a platform…"
						disabled={saving}
					/>
				</div>
			{/if}
		</div>
		{#if editing}
			<p class="text-xs text-surface-600-400">
				Intent and platform are the lookup key and cannot be edited — changing them would silently
				alter what an existing workflow resolves. Delete and re-create instead.
			</p>
		{/if}

		<Input label="Command" bind:value={fCommand} required />
		<Textarea label="Description" bind:value={fDescription} rows={2} />
		<Input label="Parser template" bind:value={fParser} hint="Optional TextFSM template name" />

		<Checkbox bind:checked={fReadOnly} label="Read-only command" />
		{#if !fReadOnly}
			<Alert tone="warning">
				Marked as changing state. The SSH policy and the step's idempotency tier both key off this.
			</Alert>
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>

<!-- Add platform. There is no platform table to write to — the string is a column
     on vendor_commands and on devices — so this names one and hands straight off
     to the create form. It becomes a platform the moment a command is saved. -->
<Modal bind:open={addTypeOpen} title="Add platform" size="sm">
	<div class="space-y-3">
		<p class="text-sm text-surface-600-400">
			Catalogue a vendor the deployment does not cover yet. The platform is selectable straight
			away and becomes permanent once you save the first command for it — until then nobody else
			sees it.
		</p>
		{#if addTypeError}<Alert tone="error">{addTypeError}</Alert>{/if}
		<Input
			label="Platform"
			bind:value={newPlatform}
			placeholder="paloalto_panos"
			required
			hint="The Netmiko device_type — lowercase letters, digits and underscores."
		/>
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (addTypeOpen = false)}>Cancel</Button>
		<Button variant="primary" onclick={confirmAddType}>
			<Plus size={15} />Add platform
		</Button>
	{/snippet}
</Modal>
