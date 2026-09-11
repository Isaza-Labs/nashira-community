<script lang="ts">
	import { page } from '$app/state';
	import {
		listPythonModules,
		createPythonModule,
		updatePythonModule,
		retryPythonModule,
		deletePythonModule,
		type PythonModule,
		type PythonModulePayload,
		type PythonModuleSource
	} from '$lib/api/python-modules.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		StatusBadge,
		IconButton,
		Input,
		Select,
		Textarea,
		Checkbox,
		Alert,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { Plus, Pencil, Trash2, Globe, RotateCw } from 'lucide-svelte';

	let items = $state<PythonModule[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<PythonModule | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fModule = $state('');
	let fDescription = $state('');
	let fRequiresNetwork = $state(false);
	let fSource = $state<PythonModuleSource>('stdlib');
	let fPipSpec = $state('');

	const columns: Column[] = [
		{ key: 'module', header: 'Module' },
		{ key: 'source', header: 'Source' },
		{ key: 'status', header: 'Status' },
		{ key: 'description', header: 'Why it is allowed' },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	// An install takes as long as pip takes, so the row has to catch up on its own —
	// an admin who has to press Refresh to find out whether a package landed will
	// reasonably conclude it never did.
	let pollTimer: ReturnType<typeof setTimeout> | null = null;
	const installing = $derived(items.some((m) => m.status === 'pending' || m.status === 'installing'));

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listPythonModules()).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	// Polls only while something is in flight, and stops the moment nothing is.
	$effect(() => {
		if (!installing) return;
		pollTimer = setTimeout(async () => {
			try {
				items = (await listPythonModules()).items;
			} catch {
				// Transient — the next tick tries again rather than surfacing a
				// network blip as a page-level error over a table that still renders.
			}
		}, 3000);
		return () => {
			if (pollTimer) clearTimeout(pollTimer);
			pollTimer = null;
		};
	});

	// The palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});

	function openCreate() {
		editing = null;
		formError = '';
		fModule = '';
		fDescription = '';
		fRequiresNetwork = false;
		fSource = 'stdlib';
		fPipSpec = '';
		modalOpen = true;
	}

	function openEdit(m: PythonModule) {
		editing = m;
		formError = '';
		fModule = m.module;
		fDescription = m.description ?? '';
		fRequiresNetwork = m.requiresNetwork;
		fSource = m.source;
		fPipSpec = m.pipSpec ?? '';
		modalOpen = true;
	}

	async function save() {
		const name = fModule.trim();
		if (!name) {
			formError = 'A module name is required.';
			return;
		}
		// No client-side mask on the module name. It mirrored the server regex, which
		// meant typing a package name like `python-dateutil` was blocked at the input
		// with a rule restated — and never told you the pip name goes in `pip_spec`.
		// The server refuses it and now says exactly that, so letting the request
		// through gets a better answer than stopping it here did.
		const spec = fPipSpec.trim();
		if (fSource === 'pip' && spec && !/^[A-Za-z0-9._\-[\]=<>!~,]+$/.test(spec)) {
			formError =
				'A pip requirement takes no spaces and no flags — just the package, extras and any version pin.';
			return;
		}
		formError = '';
		saving = true;
		const payload: PythonModulePayload = {
			module: name,
			description: fDescription,
			requiresNetwork: fRequiresNetwork,
			source: fSource,
			pipSpec: spec
		};
		try {
			if (editing) await updatePythonModule(editing.id, payload);
			else await createPythonModule(payload);
			modalOpen = false;
			toast.success(
				editing ? 'Module updated' : 'Module allowed',
				fSource === 'pip' && !editing ? { description: 'Installing in the background…' } : undefined
			);
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the module');
		} finally {
			saving = false;
		}
	}

	async function retry(m: PythonModule) {
		try {
			const updated = await retryPythonModule(m.id);
			items = items.map((x) => (x.id === updated.id ? updated : x));
			toast.success('Re-queued for install');
		} catch (e) {
			toast.fromError(e, "Couldn't re-queue the install");
		}
	}

	async function remove(m: PythonModule) {
		const ok = await confirm({
			title: 'Revoke this module?',
			message:
				m.source === 'pip'
					? `Snippets importing "${m.module}" will start failing. The installed package stays on disk until the volume is cleaned.`
					: `Snippets importing "${m.module}" will start failing at authoring time and at run time.`,
			tone: 'danger',
			confirmLabel: 'Revoke'
		});
		if (!ok) return;
		try {
			await deletePythonModule(m.id);
			items = items.filter((x) => x.id !== m.id);
			toast.success('Module revoked');
		} catch (e) {
			toast.fromError(e, "Couldn't revoke the module");
		}
	}
</script>

<svelte:head><title>Python modules · Nashira</title></svelte:head>

<PageHeader
	title="Python modules"
	description="The allowlist a python_snippet may import — nothing else gets in."
>
	{#snippet actions()}
		<RoleGate require="admin">
			<Button variant="primary" onclick={openCreate}><Plus size={15} />Allow a module</Button>
		</RoleGate>
	{/snippet}
</PageHeader>

<RoleGate require="admin">
	{#snippet fallback()}
		<div class="ui-surface px-4 py-16 text-center text-sm text-surface-600-400">
			Administrator access is required for this area.
		</div>
	{/snippet}

	{#if error}
		<ErrorState {error} onRetry={load} />
	{:else}
		<div class="space-y-3">
			<Alert tone="neutral">
				This is an <strong>allowlist</strong>, enforced twice: a scan at authoring time and an import
				hook inside the interpreter, so a dynamic import cannot slip past. The seeded set is purely
				computational — no network-capable module arrives by default, because opting into network
				access should mean an administrator added one knowingly.
			</Alert>

			<Alert tone="warning">
				A <strong>pip</strong> module is installed on the server with
				<code class="font-mono">pip install</code>, and third-party code then runs wherever snippets
				run. Add only packages you trust, and pin the version. Only a module that reached
				<strong>ready</strong> is importable — one still installing, or whose install failed, is on this
				list and not on disk.
			</Alert>

			<DataTable {loading} {columns} rows={items} rowKey={(m) => m.id} empty="No modules allowed yet.">
				{#snippet cell(row, col)}
					{#if col.key === 'module'}
						<div class="flex items-center gap-2">
							<code class="font-mono text-sm text-surface-900-100">{row.module}</code>
							{#if row.requiresNetwork}
								<Badge tone="warning">network</Badge>
							{/if}
						</div>
						{#if row.source === 'pip' && row.pipSpec && row.pipSpec !== row.module}
							<div class="font-mono text-xs text-surface-600-400">pip: {row.pipSpec}</div>
						{/if}
					{:else if col.key === 'source'}
						<Badge tone={row.source === 'pip' ? 'primary' : 'neutral'}>{row.source}</Badge>
					{:else if col.key === 'status'}
						<div class="flex items-center gap-2">
							<StatusBadge status={row.status} />
							{#if row.installedVersion}
								<span class="font-mono text-xs text-surface-600-400">{row.installedVersion}</span>
							{/if}
						</div>
						{#if row.status === 'failed' && row.error}
							<div class="mt-0.5 max-w-md truncate text-xs text-error-500" title={row.error}>
								{row.error}
							</div>
						{/if}
					{:else if col.key === 'description'}
						<span class="text-xs text-surface-600-400">{row.description ?? '—'}</span>
					{:else if col.key === 'actions'}
						<div class="flex justify-end gap-1">
							{#if row.source === 'pip' && row.status === 'failed'}
								<IconButton label="Retry install" onclick={() => retry(row)}>
									<RotateCw size={14} />
								</IconButton>
							{/if}
							<IconButton label="Edit module" onclick={() => openEdit(row)}>
								<Pencil size={14} />
							</IconButton>
							<IconButton label="Revoke module" onclick={() => remove(row)}>
								<Trash2 size={14} />
							</IconButton>
						</div>
					{/if}
				{/snippet}
			</DataTable>

			<p class="text-xs text-surface-600-400">
				Snippets that use these live under
				<a class="underline" href="/admin/snippets">Snippets</a> — a
				<code class="font-mono">python_snippet</code> also needs
				<code class="font-mono">network_enabled</code> before a network module does anything.
			</p>
		</div>
	{/if}
</RoleGate>

<Modal bind:open={modalOpen} title={editing ? 'Edit module' : 'Allow a module'} size="md">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		<Input
			label="Module"
			bind:value={fModule}
			required
			disabled={!!editing}
			hint={editing
				? 'The name is the identity of the row and cannot be changed.'
				: fSource === 'pip'
					? 'The package name pip installs, or the import name — either. python-dateutil installs and imports as dateutil; the import name is read off the installed package.'
					: 'The import name — exactly what the script writes after `import`.'}
		/>

		<Select
			label="Source"
			bind:value={fSource}
			disabled={!!editing}
			options={[
				{ value: 'stdlib', label: 'stdlib — already on the interpreter' },
				{ value: 'pip', label: 'pip — install it from PyPI' }
			]}
			hint={editing ? 'Revoke and re-add to change where a module comes from.' : ''}
		/>

		{#if fSource === 'pip'}
			<Input
				label="pip requirement"
				bind:value={fPipSpec}
				hint="Defaults to the module name. Set it when they differ — pip install pyyaml imports as yaml — or to pin a version: netmiko==4.3.0"
			/>
			<Alert tone="warning">
				This runs <code class="font-mono">pip install</code> on the server. Wheels only by default, so
				nothing is built from source during the install, but the package's code does execute once a snippet
				imports it.
			</Alert>
		{/if}

		<Textarea
			label="Why it is allowed"
			bind:value={fDescription}
			rows={2}
			hint="For whoever reviews this list later"
		/>

		<Checkbox bind:checked={fRequiresNetwork} label="This module can reach the network" />
		{#if fRequiresNetwork}
			<Alert tone="warning">
				<Globe size={14} class="mb-0.5 inline" />
				Marking it network-capable does not grant access on its own — the snippet must also have
				<code class="font-mono">network_enabled</code>, which is itself an admin act. Both switches exist
				so neither is flipped by accident.
			</Alert>
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>
