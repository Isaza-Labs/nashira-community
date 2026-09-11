<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import {
		listMcpServers,
		createMcpServer,
		updateMcpServer,
		deleteMcpServer,
		syncMcpTools,
		checkMcpServer,
		listMcpTools,
		setMcpToolEnabled,
		oauthStartMcpServer,
		MCP_AUTH_TYPES,
		type McpServer,
		type McpServerPayload,
		type McpTool,
		type McpStatus
	} from '$lib/api/mcp.api';
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
		CodeEditor,
		Checkbox,
		Alert,
		Spinner,
		ErrorState,
		confirm,
		toast,
		type Column,
		type Tone
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import AuthFields from '$lib/components/auth/AuthFields.svelte';
	import CredentialPicker from '$lib/components/credential/CredentialPicker.svelte';
	import {
		emptyDraft,
		methodForCredential,
		missingMaterial,
		HTTP_CREDENTIAL_METHODS,
		type AuthDraft
	} from '$lib/auth/draft';
	import type { Credential } from '$lib/api/credentials.api';
	import { Plus, Pencil, Trash2, Activity, RefreshCw, List, ShieldCheck } from 'lucide-svelte';
	import SectionNav from '$lib/components/layout/SectionNav.svelte';

	let items = $state<McpServer[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let checkingId = $state<string | null>(null);
	let syncingId = $state<string | null>(null);
	let authorizingId = $state<string | null>(null);

	let modalOpen = $state(false);
	let editing = $state<McpServer | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fDescription = $state('');
	let fUrl = $state('');
	// Auth is edited as a source ("where do the credentials come from") plus either a
	// stored credential or typed fields; the JSON editor stays as an escape hatch.
	let fAuthSource = $state<'none' | 'stored' | 'custom' | 'json'>('none');
	let fAuthType = $state('none');
	let fDraft = $state<AuthDraft>(emptyDraft());
	let fCredentialId = $state('');
	let fCredential = $state<Credential | null>(null);
	let fAuthConfig = $state('');
	let fClearAuth = $state(false);
	let fHeaders = $state('');
	let fTlsSkip = $state(false);
	let fAllowPrivate = $state(false);
	let fTrustHints = $state(false);
	let fEnabled = $state(true);

	let toolsOpen = $state(false);
	let toolsFor = $state<McpServer | null>(null);
	let tools = $state<McpTool[]>([]);
	let toolsLoading = $state(false);
	let togglingId = $state<string | null>(null);

	const authSourceOptions = [
		{ value: 'none', label: 'No authentication' },
		{ value: 'stored', label: 'Use a stored credential' },
		{ value: 'custom', label: 'Enter credentials here' },
		{ value: 'json', label: 'Advanced: edit the JSON' }
	];

	// Picking a credential preselects the scheme it implies; still overridable.
	$effect(() => {
		if (fAuthSource === 'stored' && fCredential) {
			fAuthType = methodForCredential('mcp', fCredential.authMethod);
		}
	});

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'url', header: 'Endpoint' },
		{ key: 'auth', header: 'Auth' },
		{ key: 'tools', header: 'Tools', sortValue: (s: McpServer) => s.toolCount },
		{ key: 'status', header: 'Status' },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	function statusTone(s: McpStatus): Tone {
		if (s === 'healthy') return 'success';
		if (s === 'degraded' || s === 'needs_authorization') return 'warning';
		if (s === 'unreachable') return 'error';
		return 'neutral';
	}

	// Begin the OAuth consent round-trip: the browser leaves for the authorization
	// server and the backend's callback bounces it back here with a query flag.
	async function authorize(s: McpServer) {
		authorizingId = s.id;
		try {
			window.location.href = await oauthStartMcpServer(s.id);
		} catch (e) {
			toast.fromError(e, 'Could not start authorization');
			authorizingId = null;
		}
	}

	// Complete the round-trip when the callback lands us back on this page.
	$effect(() => {
		const authorized = page.url.searchParams.get('mcp_authorized');
		const err = page.url.searchParams.get('mcp_error');
		if (!authorized && !err) return;
		if (authorized) toast.success('MCP server authorized');
		if (err) toast.error(`Authorization failed: ${err}`);
		void goto('/admin/mcp', { replaceState: true });
	});

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listMcpServers()).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	function reset() {
		formError = '';
		fName = '';
		fDescription = '';
		fUrl = '';
		fAuthSource = 'none';
		fAuthType = 'none';
		fDraft = emptyDraft();
		fCredentialId = '';
		fCredential = null;
		fAuthConfig = '';
		fClearAuth = false;
		fHeaders = '';
		fTlsSkip = false;
		fAllowPrivate = false;
		fTrustHints = false;
		fEnabled = true;
	}

	function openCreate() {
		editing = null;
		reset();
		modalOpen = true;
	}

	function openEdit(s: McpServer) {
		editing = s;
		reset();
		fName = s.name;
		fDescription = s.description ?? '';
		fUrl = s.url;
		fAuthType = s.authType;
		fTlsSkip = s.tlsSkipVerify;
		fAllowPrivate = s.allowPrivateNetwork;
		fTrustHints = s.trustToolHints;
		fEnabled = s.enabled;
		// A linked credential is visible on read; typed material is not — the API never
		// returns it, so those fields stay blank and blank means "keep".
		fCredentialId = s.authCredentialId ?? '';
		fAuthSource = s.authCredentialId ? 'stored' : s.authType !== 'none' ? 'custom' : 'none';
		modalOpen = true;
	}

	function payload(): McpServerPayload {
		return {
			name: fName.trim(),
			description: fDescription,
			url: fUrl.trim(),
			authType: fAuthSource === 'none' ? 'none' : fAuthType,
			authDraft: fDraft,
			authCredentialId: fAuthSource === 'stored' ? fCredentialId : '',
			authConfigOverride: fAuthSource === 'json' ? fAuthConfig : null,
			clearAuthConfig: fClearAuth,
			headers: fHeaders,
			tlsSkipVerify: fTlsSkip,
			allowPrivateNetwork: fAllowPrivate,
			trustToolHints: fTrustHints,
			enabled: fEnabled
		};
	}

	async function save() {
		if (!fName.trim()) {
			formError = 'Name is required.';
			return;
		}
		if (!fUrl.trim()) {
			formError = 'Endpoint URL is required.';
			return;
		}
		if (fAuthSource === 'stored' && !fCredentialId) {
			formError = 'Pick a credential, or choose another source.';
			return;
		}
		const missing =
			fAuthSource === 'custom'
				? missingMaterial('mcp', fAuthType, fDraft, false, !!editing)
				: null;
		if (missing) {
			formError = missing;
			return;
		}
		formError = '';
		saving = true;
		try {
			if (editing) await updateMcpServer(editing.id, payload());
			else await createMcpServer(payload());
			modalOpen = false;
			toast.success(editing ? 'MCP server updated' : 'MCP server registered');
			await load();
		} catch (e) {
			// A JSON typo in auth config is a field problem, not a request failure —
			// keep the modal open and say where it is.
			if (e instanceof SyntaxError) {
				formError = `Auth config is not valid JSON: ${e.message}`;
			} else {
				toast.fromError(e, 'Failed to save the MCP server');
			}
		} finally {
			saving = false;
		}
	}

	async function remove(s: McpServer) {
		const ok = await confirm({
			title: 'Delete MCP server?',
			message: `"${s.name}" and its ${s.toolCount} cached tool(s) will be removed. Workflows or agent turns that call those tools will start failing.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteMcpServer(s.id);
			items = items.filter((x) => x.id !== s.id);
			toast.success('MCP server deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the MCP server");
		}
	}

	async function check(s: McpServer) {
		checkingId = s.id;
		try {
			const h = await checkMcpServer(s.id);
			if (h.status === 'healthy') {
				toast.success(`${s.name}: healthy, ${h.toolCount} tool(s) (${h.elapsedMs} ms)`);
			} else {
				toast.error(`${s.name}: ${h.error ?? h.status}`);
			}
			await load();
		} catch (e) {
			toast.fromError(e, "Couldn't reach the MCP server");
		} finally {
			checkingId = null;
		}
	}

	async function sync(s: McpServer) {
		syncingId = s.id;
		try {
			const r = await syncMcpTools(s.id);
			toast.success(
				`${s.name}: ${r.discovered} tool(s) — +${r.created} new, ${r.updated} updated, ${r.disappeared} retired`
			);
			await load();
		} catch (e) {
			toast.fromError(e, "Couldn't sync the tool catalog");
		} finally {
			syncingId = null;
		}
	}

	async function openTools(s: McpServer) {
		toolsFor = s;
		tools = [];
		toolsOpen = true;
		toolsLoading = true;
		try {
			// Retired tools included: an operator needs to see that a tool a workflow
			// depends on has gone away.
			tools = (await listMcpTools(s.id, true)).items;
		} catch (e) {
			toast.fromError(e, "Couldn't load the tool catalog");
		} finally {
			toolsLoading = false;
		}
	}

	async function toggleTool(t: McpTool) {
		if (!toolsFor) return;
		togglingId = t.id;
		try {
			const updated = await setMcpToolEnabled(toolsFor.id, t.id, !t.enabled);
			tools = tools.map((x) => (x.id === t.id ? updated : x));
		} catch (e) {
			toast.fromError(e, "Couldn't change the tool");
		} finally {
			togglingId = null;
		}
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>MCP servers · Nashira</title></svelte:head>

<SectionNav id="integrations" />

<PageHeader
	title="MCP servers"
	description="External Model Context Protocol servers. Nashira connects as a client, caches their tools, and lets the agent call them."
>
	{#snippet actions()}
		<RoleGate require="admin">
			<Button variant="primary" onclick={openCreate}><Plus size={15} />Register server</Button>
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
				A registered server can run code on Nashira's behalf. The protocol does not distinguish
				reads from writes, so every <code>mcp_call</code> asks the user to confirm.
			</Alert>

			<DataTable {loading} {columns} rows={items} rowKey={(s) => s.id} empty="No MCP servers — connect one and its tools become available to the agent and to mcp_call snippets.">
				{#snippet cell(row, col)}
					{#if col.key === 'name'}
						<div class="font-medium">{row.name}</div>
						{#if row.description}
							<div class="max-w-[20rem] truncate text-xs text-surface-600-400">{row.description}</div>
						{/if}
					{:else if col.key === 'url'}
						<code class="text-xs text-surface-600-400">{row.url}</code>
					{:else if col.key === 'auth'}
						<div class="flex items-center gap-1.5">
							<Badge>{row.authType}</Badge>
							{#if row.authType !== 'none' && !row.hasCredentials}
								<span class="text-xs text-warning-600-400">not set</span>
							{/if}
						</div>
					{:else if col.key === 'tools'}
						{#if row.toolsSyncedAt}
							<span class="text-xs text-surface-600-400" title={new Date(row.toolsSyncedAt).toLocaleString()}>
								{row.toolCount}
							</span>
						{:else}
							<span class="text-xs text-warning-600-400">never synced</span>
						{/if}
					{:else if col.key === 'status'}
						<div class="flex items-center gap-1.5">
							<Badge tone={statusTone(row.status)}>{row.status}</Badge>
							{#if !row.enabled}<Badge tone="warning">disabled</Badge>{/if}
							{#if row.tlsSkipVerify}<Badge tone="error">no TLS check</Badge>{/if}
						</div>
						{#if row.lastCheckError}
							<div class="mt-0.5 max-w-[28rem] truncate text-xs text-error-600-400" title={row.lastCheckError}>
								{row.lastCheckError}
							</div>
						{/if}
					{:else if col.key === 'actions'}
						<div class="flex justify-end gap-1">
							{#if row.authType === 'oauth_authorization_code'}
								<IconButton
									label={`Authorize ${row.name}`}
									disabled={authorizingId === row.id}
									onclick={() => authorize(row)}
								>
									{#if authorizingId === row.id}<Spinner size="sm" />{:else}
										<ShieldCheck
											size={14}
											class={row.status === 'needs_authorization' ? 'text-warning-600-400' : ''}
										/>
									{/if}
								</IconButton>
							{/if}
							<IconButton label={`Browse ${row.name} tools`} onclick={() => openTools(row)}>
								<List size={14} />
							</IconButton>
							<IconButton
								label={`Test ${row.name}`}
								disabled={checkingId === row.id}
								onclick={() => check(row)}
							>
								{#if checkingId === row.id}<Spinner size="sm" />{:else}<Activity size={14} />{/if}
							</IconButton>
							<IconButton
								label={`Sync ${row.name} tools`}
								disabled={syncingId === row.id}
								onclick={() => sync(row)}
							>
								{#if syncingId === row.id}<Spinner size="sm" />{:else}<RefreshCw size={14} />{/if}
							</IconButton>
							<IconButton label="Edit server" onclick={() => openEdit(row)}>
								<Pencil size={14} />
							</IconButton>
							<IconButton label="Delete server" onclick={() => remove(row)}>
								<Trash2 size={14} />
							</IconButton>
						</div>
					{/if}
				{/snippet}
			</DataTable>
		</div>
	{/if}
</RoleGate>

<Modal bind:open={modalOpen} title={editing ? 'Edit MCP server' : 'Register MCP server'} size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		<Input label="Name" bind:value={fName} required />
		<Input
			label="Endpoint URL"
			bind:value={fUrl}
			required
			hint="Absolute http(s) URL. Streamable HTTP / SSE — stdio servers are not supported."
		/>
		<Textarea label="Description" bind:value={fDescription} rows={2} />

		<fieldset class="ui-surface space-y-3 rounded-lg p-3">
			<legend class="px-1 text-sm font-medium">Authentication</legend>

			<Select label="Credentials" bind:value={fAuthSource} options={authSourceOptions} />

			{#if fAuthSource === 'stored'}
				<CredentialPicker
					bind:value={fCredentialId}
					bind:selected={fCredential}
					label="Stored credential"
					emptyLabel="— pick a credential —"
					allowedMethods={HTTP_CREDENTIAL_METHODS}
					showMethod
					hint="Managed in Administration → Credentials. The secret never leaves the server."
				/>
				{#if fCredential}
					<AuthFields dialect="mcp" bind:method={fAuthType} bind:draft={fDraft} shapeOnly />
				{/if}
			{:else if fAuthSource === 'custom'}
				<AuthFields dialect="mcp" bind:method={fAuthType} bind:draft={fDraft} editing={!!editing} />
			{:else if fAuthSource === 'json'}
				<CodeEditor
					label="Auth config"
					language="json"
					bind:value={fAuthConfig}
					rows={7}
					hint={editing
						? 'Leave blank to keep the stored credentials — the API never returns them.'
						: 'JSON. Encrypted at rest.'}
				/>
				<p class="text-xs text-surface-600-400">
					Fields by type: <code>api_key</code> → <code>api_key</code>, <code>api_key_header</code>;
					<code>bearer</code> → <code>token</code>; <code>basic</code> → <code>username</code>,
					<code>password</code>; <code>headers</code> → <code>secret_headers</code>;
					<code>oauth_client_credentials</code> → <code>token_url</code>, <code>client_id</code>,
					<code>client_secret</code>, <code>scope</code>;
					<code>oauth_authorization_code</code> adds <code>authorization_endpoint</code> and
					<code>redirect_uri</code> (all optional — discovery and dynamic registration fill the gaps).
				</p>
			{/if}

			{#if fAuthType === 'oauth_authorization_code' && fAuthSource === 'custom'}
				<Alert tone="neutral">
					Save first, then use the <strong>Authorize</strong> button on the server row to
					complete consent in the browser.
				</Alert>
			{/if}

			{#if editing && editing.hasCredentials && fAuthSource !== 'stored'}
				<Checkbox bind:checked={fClearAuth} label="Remove the stored credentials" />
			{/if}
		</fieldset>

		<CodeEditor
			label="Static headers"
			language="json"
			bind:value={fHeaders}
			rows={3}
			hint="Optional JSON object of non-secret headers."
		/>

		<div class="flex flex-wrap gap-4 pt-1">
			<Checkbox bind:checked={fEnabled} label="Enabled" />
			<Checkbox bind:checked={fTlsSkip} label="Skip TLS verification" />
			<Checkbox bind:checked={fAllowPrivate} label="Allow private network" />
			<Checkbox bind:checked={fTrustHints} label="Trust this server's read-only hints" />
		</div>

		{#if fTlsSkip}
			<Alert tone="warning">
				TLS verification is off. Credentials sent to this server can be intercepted.
			</Alert>
		{/if}
		{#if fAllowPrivate}
			<Alert tone="warning">
				This server may live on a private (RFC-1918) address. Loopback and the cloud metadata
				address stay blocked regardless.
			</Alert>
		{/if}
		{#if fTrustHints}
			<Alert tone="warning">
				Tools this server annotates as read-only will run without asking the user. The
				annotation is written by the server itself, so this says you run it and believe it.
			</Alert>
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>

<Modal bind:open={toolsOpen} title={`${toolsFor?.name ?? ''} — tools`} size="lg">
	{#if toolsLoading}
		<div class="flex justify-center py-10"><Spinner /></div>
	{:else if tools.length === 0}
		<div class="px-4 py-10 text-center text-sm text-surface-600-400">
			No tools cached yet. Run the sync to discover them.
		</div>
	{:else}
		<div class="max-h-[26rem] space-y-1 overflow-y-auto">
			{#each tools as t (t.id)}
				<div class="flex items-start gap-3 rounded-lg border border-surface-100-900 px-3 py-2">
					<div class="min-w-0 flex-1">
						<div class="flex items-center gap-2">
							<code class="text-sm font-medium">{t.name}</code>
							{#if t.disappearedAt}<Badge tone="error">gone</Badge>{/if}
							{#if !t.enabled}<Badge tone="warning">disabled</Badge>{/if}
						</div>
						{#if t.description}
							<div class="mt-0.5 text-xs text-surface-600-400">{t.description}</div>
						{/if}
					</div>
					<Button
						variant="secondary"
						size="sm"
						loading={togglingId === t.id}
						onclick={() => toggleTool(t)}
					>
						{t.enabled ? 'Disable' : 'Enable'}
					</Button>
				</div>
			{/each}
		</div>
	{/if}
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (toolsOpen = false)}>Close</Button>
	{/snippet}
</Modal>
