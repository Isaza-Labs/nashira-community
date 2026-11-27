<script lang="ts">
	// The Environments tab of /admin/python: virtualenvs built with uv. Mounted only
	// while its tab is open, so its polling stops when the tab is left.
	import { page } from '$app/state';
	import {
		listPythonEnvironments,
		createPythonEnvironment,
		updatePythonEnvironment,
		rebuildPythonEnvironment,
		deletePythonEnvironment,
		type PythonEnvironment,
		type PythonEnvironmentPayload
	} from '$lib/api/python-environments.api';
	import {
		Button,
		Modal,
		DataTable,
		Badge,
		StatusBadge,
		IconButton,
		Input,
		Textarea,
		Checkbox,
		Alert,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { Plus, Pencil, Trash2, RotateCw, ListTree, LifeBuoy } from 'lucide-svelte';

	let items = $state<PythonEnvironment[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<PythonEnvironment | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fDescription = $state('');
	let fPythonVersion = $state('');
	let fRequirements = $state('');
	let fSshFallback = $state(false);

	// The detail view: what a build actually installed, and why the last one failed.
	let detailOpen = $state(false);
	let detail = $state<PythonEnvironment | null>(null);

	const PACKAGES_TAB = '/admin/python?tab=packages';

	const NAME_PATTERN = /^[a-z0-9][a-z0-9-]{1,48}[a-z0-9]$/;

	const columns: Column[] = [
		{ key: 'name', header: 'Environment' },
		{ key: 'python', header: 'Python' },
		{ key: 'status', header: 'Status' },
		{ key: 'description', header: 'Why it exists' },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	// A build takes as long as uv takes, so the table catches up on its own while
	// anything is pending or building — and stops polling the moment nothing is.
	let pollTimer: ReturnType<typeof setTimeout> | null = null;
	const building = $derived(items.some((e) => e.status === 'pending' || e.status === 'building'));

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listPythonEnvironments()).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	$effect(() => {
		if (!building) return;
		pollTimer = setTimeout(async () => {
			try {
				items = (await listPythonEnvironments()).items;
				// Keep an open detail view in step with the row it shows.
				if (detail) detail = items.find((e) => e.id === detail?.id) ?? detail;
			} catch {
				// Transient — the next tick tries again rather than turning a network
				// blip into a page-level error over a table that still renders.
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
		fName = '';
		fDescription = '';
		fPythonVersion = '';
		fRequirements = '';
		fSshFallback = false;
		modalOpen = true;
	}

	function openEdit(e: PythonEnvironment) {
		editing = e;
		formError = '';
		fName = e.name;
		fDescription = e.description ?? '';
		fPythonVersion = e.pythonVersion ?? '';
		fRequirements = e.requirements;
		fSshFallback = e.isSshFallback;
		modalOpen = true;
	}

	function openDetail(e: PythonEnvironment) {
		detail = e;
		detailOpen = true;
	}

	async function save() {
		const name = fName.trim();
		if (!editing && !NAME_PATTERN.test(name)) {
			formError =
				'The name is 3–50 lowercase letters, digits and dashes, starting and ending with a letter or digit — for example ssh-legacy.';
			return;
		}
		if (!fRequirements.trim()) {
			formError = 'List at least one package, one per line, as in a requirements.txt.';
			return;
		}
		formError = '';
		saving = true;
		const payload: PythonEnvironmentPayload = {
			name,
			description: fDescription,
			pythonVersion: fPythonVersion,
			requirements: fRequirements,
			isSshFallback: fSshFallback
		};
		try {
			const saved = editing
				? await updatePythonEnvironment(editing.id, payload)
				: await createPythonEnvironment(payload);
			modalOpen = false;
			const queued = saved.status === 'pending';
			toast.success(
				editing ? 'Environment updated' : 'Environment created',
				queued ? { description: 'Building in the background…' } : undefined
			);
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the environment');
		} finally {
			saving = false;
		}
	}

	async function rebuild(e: PythonEnvironment) {
		try {
			const updated = await rebuildPythonEnvironment(e.id);
			items = items.map((x) => (x.id === updated.id ? updated : x));
			toast.success('Queued for rebuild');
		} catch (err) {
			toast.fromError(err, "Couldn't queue the rebuild");
		}
	}

	async function remove(e: PythonEnvironment) {
		const ok = await confirm({
			title: 'Delete this environment?',
			message: e.isSshFallback
				? `"${e.name}" is the SSH fallback. Without it, a device whose SSH server only offers ssh-rsa fails instead of being retried. Devices and snippets still using it must be pointed elsewhere first.`
				: `Devices and snippets still using "${e.name}" must be pointed elsewhere first. Its build stays on disk until the volume is cleaned.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deletePythonEnvironment(e.id);
			items = items.filter((x) => x.id !== e.id);
			toast.success('Environment deleted');
		} catch (err) {
			toast.fromError(err, "Couldn't delete the environment");
		}
	}

	function when(iso: string | null): string {
		return iso ? new Date(iso).toLocaleString() : '—';
	}
</script>

<div class="mb-4 flex flex-wrap items-center justify-between gap-3">
	<p class="text-sm text-surface-600-400">Pinned package sets, built with uv, that SSH devices and Python snippets can run under.</p>
	<RoleGate require="admin">
		<Button variant="primary" onclick={openCreate}><Plus size={15} />New environment</Button>
	</RoleGate>
</div>

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
				Everything runs on the <strong>system</strong> Python unless a device or a snippet picks an
				environment here. Each environment is built on the server with
				<code class="font-mono">uv</code> and swapped into place only once the build finishes, so a running
				job never sees a half-built one. Only a <strong>ready</strong> environment is used.
			</Alert>

			<Alert tone="warning">
				Building an environment installs its packages on the server, and their code then runs wherever
				SSH sessions and snippets run. Pin versions, and add only packages you trust. A
				<code class="font-mono">python_snippet</code> still imports only what the Python modules allowlist
				permits, whatever the environment contains.
			</Alert>

			<DataTable
				{loading}
				{columns}
				rows={items}
				rowKey={(e) => e.id}
				empty="No environments yet — everything uses the system Python."
			>
				{#snippet cell(row, col)}
					{#if col.key === 'name'}
						<div class="flex items-center gap-2">
							<code class="font-mono text-sm text-surface-900-100">{row.name}</code>
							{#if row.isSshFallback}
								<Badge tone="primary">SSH fallback</Badge>
							{/if}
						</div>
					{:else if col.key === 'python'}
						<span class="font-mono text-xs text-surface-600-400">{row.pythonVersion ?? 'image python3'}</span>
					{:else if col.key === 'status'}
						<div class="flex items-center gap-2">
							<StatusBadge status={row.status} />
							{#if row.builtAt && row.status === 'ready'}
								<span class="text-xs text-surface-600-400" title="Last successful build">
									{when(row.builtAt)}
								</span>
							{/if}
						</div>
						{#if row.status === 'failed' && row.lastError}
							<div class="mt-0.5 max-w-md truncate text-xs text-error-500" title={row.lastError}>
								{row.lastError}
							</div>
						{/if}
					{:else if col.key === 'description'}
						<span class="text-xs text-surface-600-400">{row.description ?? '—'}</span>
					{:else if col.key === 'actions'}
						<div class="flex justify-end gap-1">
							<IconButton label="Resolved packages and last error" onclick={() => openDetail(row)}>
								<ListTree size={14} />
							</IconButton>
							{#if row.status === 'ready' || row.status === 'failed'}
								<IconButton label="Rebuild environment" onclick={() => rebuild(row)}>
									<RotateCw size={14} />
								</IconButton>
							{/if}
							<IconButton label="Edit environment" onclick={() => openEdit(row)}>
								<Pencil size={14} />
							</IconButton>
							<IconButton label="Delete environment" onclick={() => remove(row)}>
								<Trash2 size={14} />
							</IconButton>
						</div>
					{/if}
				{/snippet}
			</DataTable>

			<p class="text-xs text-surface-600-400">
				Pick an environment on a <a class="underline" href="/devices">device</a> (for its SSH sessions) or on a
				<code class="font-mono">python_snippet</code> under
				<a class="underline" href="/admin/snippets">Snippets</a>. The environment marked
				<strong>SSH fallback</strong> is also used automatically, once, when an SSH session fails because the
				device's host key algorithm is not supported — the seeded <code class="font-mono">ssh-legacy</code>
				keeps paramiko below 5, which removed <code class="font-mono">ssh-rsa</code>.
			</p>
		</div>
	{/if}
</RoleGate>

<!-- What the Packages allowlist adds to an environment. Read-only here: it is set on
     each allowlist row ("Install in"), and the build installs it with the requirements. -->
{#snippet included(env: PythonEnvironment)}
	<div>
		<div class="mb-1 flex items-center justify-between gap-2">
			<span class="text-xs font-medium text-surface-700-300">Included from Packages</span>
			<a class="text-xs underline" href={PACKAGES_TAB}>Edit on the Packages tab</a>
		</div>
		{#if env.includedPackages.length > 0}
			<ul class="max-h-48 overflow-auto rounded-md bg-surface-100-900 p-2 font-mono text-xs">
				{#each env.includedPackages as spec (spec)}
					<li>{spec}</li>
				{/each}
			</ul>
			<p class="mt-1 text-xs text-surface-600-400">
				Installed with the requirements above (duplicates dropped) and covered by the build hash. Changing a
				package's targets or requirement on the Packages tab rebuilds this environment.
			</p>
		{:else}
			<p class="text-xs text-surface-600-400">
				No allowlist package targets this environment. Choose it under "Install in" on a pip module to add one.
			</p>
		{/if}
	</div>
{/snippet}

<Modal bind:open={modalOpen} title={editing ? 'Edit environment' : 'New environment'} size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		<Input
			label="Name"
			bind:value={fName}
			required
			disabled={!!editing}
			placeholder="ssh-legacy"
			hint={editing
				? 'The name is the identity of the environment and cannot be changed.'
				: 'Lowercase letters, digits and dashes. It is what devices and snippets show.'}
		/>

		<Textarea
			label="Description"
			bind:value={fDescription}
			rows={2}
			hint="Why this environment exists, for whoever reviews it later"
		/>

		<Input
			label="Python version"
			bind:value={fPythonVersion}
			placeholder="image python3"
			hint="Blank uses the image's python3. A version such as 3.12 is downloaded by uv if the image does not have it."
		/>

		<Textarea
			label="Requirements"
			bind:value={fRequirements}
			rows={8}
			required
			placeholder={'netmiko>=4,<5\nparamiko>=3.5,<5\ntextfsm>=1.1\nntc-templates>=4'}
			hint="requirements.txt format, one package per line. Index URLs are allowed; credentials in them are stored and shown masked (***). Changing this or the Python version rebuilds the environment."
		/>

		{#if editing}
			{@render included(editing)}
		{/if}

		<Checkbox bind:checked={fSshFallback} label="Use as the SSH fallback" />
		{#if fSshFallback}
			<Alert tone="neutral">
				<LifeBuoy size={14} class="mb-0.5 inline" />
				When an SSH session fails with paramiko's "Incompatible ssh peer (no acceptable host key)", it is
				retried once under this environment, and the output says so. Only one environment can be the fallback.
			</Alert>
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>

<Modal bind:open={detailOpen} title={detail ? `${detail.name} — build` : 'Build'} size="lg">
	{#if detail}
		<div class="space-y-3 text-sm">
			<div class="flex flex-wrap items-center gap-2">
				<StatusBadge status={detail.status} />
				<span class="text-xs text-surface-600-400">Last successful build: {when(detail.builtAt)}</span>
			</div>

			{#if detail.lastError}
				<div>
					<div class="mb-1 text-xs font-medium text-surface-700-300">Last error</div>
					<pre
						class="max-h-48 overflow-auto rounded-md bg-surface-100-900 p-2 font-mono text-xs whitespace-pre-wrap text-error-600-400">{detail.lastError}</pre>
				</div>
			{/if}

			<div>
				<div class="mb-1 text-xs font-medium text-surface-700-300">Resolved packages (uv pip freeze)</div>
				{#if detail.resolvedPackages}
					<pre
						class="max-h-80 overflow-auto rounded-md bg-surface-100-900 p-2 font-mono text-xs">{detail.resolvedPackages}</pre>
				{:else}
					<p class="text-xs text-surface-600-400">Not built yet.</p>
				{/if}
			</div>

			<div>
				<div class="mb-1 text-xs font-medium text-surface-700-300">Requirements</div>
				<pre
					class="max-h-48 overflow-auto rounded-md bg-surface-100-900 p-2 font-mono text-xs">{detail.requirements}</pre>
			</div>

			{@render included(detail)}
		</div>
	{/if}

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (detailOpen = false)}>Close</Button>
	{/snippet}
</Modal>
