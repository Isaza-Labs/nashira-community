<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import {
		listSpecs,
		getSpec,
		createSpec,
		updateSpec,
		deleteSpec,
		SPEC_AUTH_TYPES,
		type Spec
	} from '$lib/api/specs.api';
	import { listIntegrations, type Integration } from '$lib/api/integrations.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		IconButton,
		Input,
		Select,
		CodeEditor,
		Checkbox,
		Alert,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import { Plus, Pencil, Trash2, ExternalLink } from 'lucide-svelte';
	import SectionNav from '$lib/components/layout/SectionNav.svelte';
	import FilterChip from '$lib/components/layout/FilterChip.svelte';

	let items = $state<Spec[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<Spec | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fApi = $state('');
	let fContent = $state('');
	let fBaseUrl = $state('');
	let fAuthType = $state('none');
	let fVerify = $state(true);
	let fAllowPrivate = $state(true);
	let fIntegrationId = $state('');

	// Linking a spec to an integration makes it inherit that integration's base URL
	// and credentials whenever the spec does not carry its own.
	let integrations = $state<Integration[]>([]);
	const integrationOptions = $derived([
		{ value: '', label: '— none (self-contained) —' },
		...integrations.map((i) => ({ value: i.id, label: i.name }))
	]);
	const linked = $derived(integrations.find((i) => i.id === fIntegrationId) ?? null);

	// Arriving from an integration ("3 specs") shows just those specs. The filter
	// is a chip rather than an invisible narrowing — see FilterChip.
	const filterId = $derived(page.url.searchParams.get('integration'));
	const filterIntegration = $derived(integrations.find((i) => i.id === filterId) ?? null);
	// Narrowed server-side (see load): unfiltered, this page asks for global specs
	// only. A spec bound to an integration belongs to that integration's screen —
	// listed here it is noise, and editing it from a page that does not name the
	// system is how its auth ends up wrong.
	const visible = $derived(items);

	// OpenAPI specs are YAML or JSON — highlight whichever the content looks like.
	const contentLanguage = $derived(fContent.trimStart().startsWith('{') ? 'json' : 'yaml');

	$effect(() => {
		if (fIntegrationId && fAuthType !== 'none') fAuthType = 'none';
	});

	// Loading a file names the API from the filename when creating and the field
	// is still empty (on edit the name is fixed, so nothing is touched).
	function onContentFile(filename: string) {
		if (!editing && !fApi.trim()) fApi = filename.replace(/\.(ya?ml|json)$/i, '');
	}

	const columns: Column[] = [
		{ key: 'api', header: 'API' },
		{ key: 'operationCount', header: 'Operations' },
		{ key: 'authType', header: 'Auth' },
		{ key: 'actions', header: '', align: 'right' }
	];

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listSpecs(100, 0, { integrationId: filterId })).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		// See the skills page: the scope decides what load() requests, so the
		// dependency is named rather than left buried in a call argument.
		filterId;
		load();
		// Best effort: without integrations the picker just offers "none", which is
		// exactly what a self-contained spec wants anyway.
		listIntegrations()
			.then((r) => (integrations = r.items))
			.catch(() => (integrations = []));
	});

	function openCreate() {
		editing = null;
		formError = '';
		fApi = '';
		fContent = '';
		fBaseUrl = '';
		fAuthType = 'none';
		fVerify = true;
		fAllowPrivate = true;
		// Default to the scope being viewed. Left at global, pressing New on a
		// filtered page and saving sends the admin straight back out to the global
		// list — which reads as the app having un-scoped what they just made.
		fIntegrationId = filterId ?? '';
		modalOpen = true;
	}

	async function openEdit(s: Spec) {
		try {
			const d = await getSpec(s.id);
			editing = s;
			formError = '';
			fApi = d.api;
			fContent = d.content;
			fBaseUrl = d.baseUrl ?? '';
			fAuthType = d.authType;
			fVerify = d.verifySsl;
			fAllowPrivate = d.allowPrivateNetwork;
			fIntegrationId = d.integrationId ?? '';
			modalOpen = true;
		} catch (e) {
			toast.fromError(e, "Couldn't load the spec");
		}
	}

	async function save() {
		if (!editing && !fApi.trim()) {
			formError = 'API name is required.';
			return;
		}
		if (!fContent.trim()) {
			formError = 'Spec content is required.';
			return;
		}
		saving = true;
		try {
			const authType = fIntegrationId ? 'none' : fAuthType.trim();
			const common = {
				content: fContent,
				baseUrl: fBaseUrl.trim(),
				authType,
				verifySsl: fVerify,
				allowPrivateNetwork: fAllowPrivate,
				integrationId: fIntegrationId
			};
			if (editing) await updateSpec(editing.id, common);
			else await createSpec({ api: fApi.trim(), ...common });
			modalOpen = false;
			// Same as the skills page: this list is global-only, so a spec just bound
			// to an integration is not in it any more. Go where the row went.
			// Compare SCOPES, not "is a scope set": clearing the scope on a filtered
			// page moves the row to the global list, which is just as invisible from
			// here as the other direction.
			const newScope = fIntegrationId || null;
			const listScope = filterId || null;
			if (newScope !== listScope) {
				const owner = integrations.find((i) => i.id === newScope);
				toast.success(editing ? 'Spec updated' : 'Spec created', {
					description: newScope
						? `Bound to ${owner?.name ?? 'an integration'} — showing its specs.`
						: 'Now global — showing the global specs.'
				});
				await goto(newScope ? `/admin/specs?integration=${newScope}` : '/admin/specs');
				return;
			}
			toast.success(editing ? 'Spec updated' : 'Spec created');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the spec');
		} finally {
			saving = false;
		}
	}

	async function remove(s: Spec) {
		const ok = await confirm({
			title: 'Delete API spec?',
			message: `"${s.api}" and its ${s.operationCount} operations will be removed.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteSpec(s.id);
			items = items.filter((x) => x.id !== s.id);
			toast.success('Spec deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the spec");
		}
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>API Specs · Nashira</title></svelte:head>

<SectionNav id="ai-studio" />

<PageHeader title="API Specs" description="OpenAPI specs the agent can call.">
	{#snippet actions()}
		<Button variant="primary" onclick={openCreate}><Plus size={15} />New spec</Button>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<div class="space-y-3">
		{#if filterId}
			<div class="flex flex-wrap items-center gap-3">
				<FilterChip
					label="Integration"
					value={filterIntegration?.name ?? 'unknown'}
					clearHref="/admin/specs"
					count={visible.length}
				/>
				<a
					class="inline-flex items-center gap-1 text-xs text-surface-600-400 underline hover:text-primary-700-300"
					href="/admin/integrations"
				>
					Back to integrations<ExternalLink size={11} />
				</a>
			</div>
		{/if}

		<DataTable
			{loading}
			{columns}
			rows={visible}
			rowKey={(s) => s.id}
			empty={filterId
				? 'This integration has no specs yet — without one it has no action catalogue.'
				: 'No global API specs. Specs that belong to an integration live on that integration.'}
		>
			{#snippet cell(row, col)}
				{#if col.key === 'api'}
					<span class="font-medium">{row.api}</span>
				{:else if col.key === 'operationCount'}
					<span class="tabular-nums text-surface-600-400">{row.operationCount}</span>
				{:else if col.key === 'authType'}
					<Badge>{row.integrationId && row.authType === 'none' ? 'integration' : row.authType}</Badge>
				{:else if col.key === 'actions'}
					<div class="flex justify-end gap-1">
						<IconButton label="Edit spec" onclick={() => openEdit(row)}><Pencil size={14} /></IconButton>
						<IconButton label="Delete spec" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
					</div>
				{/if}
			{/snippet}
		</DataTable>
	</div>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit spec' : 'New spec'} size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}
		<div class="grid gap-3 sm:grid-cols-2">
			<Input label="API name" bind:value={fApi} disabled={!!editing} required={!editing} />
			<Select label="Integration" bind:value={fIntegrationId} options={integrationOptions} />
		</div>
		{#if linked}
			<Alert tone="neutral">
				This spec will use <strong>{linked.name}</strong>'s URL ({linked.baseUrl}) and credentials.
				Leave <strong>Base URL</strong> blank unless this OpenAPI document must override the
				integration endpoint.
			</Alert>
		{:else}
			<Select
				label="Auth type"
				bind:value={fAuthType}
				options={SPEC_AUTH_TYPES}
				hint="Only for self-contained specs. Linked specs inherit integration credentials."
			/>
		{/if}
		<Input label="Base URL" bind:value={fBaseUrl} hint="Overrides the spec's servers and the linked integration" />
		<CodeEditor
			label="Spec content"
			bind:value={fContent}
			language={contentLanguage}
			rows={16}
			accept=".yaml,.yml,.json"
			onfile={onContentFile}
			hint="OpenAPI JSON or YAML — paste it or load a spec file"
		/>
		<Checkbox bind:checked={fVerify} label="Verify TLS certificate" />
		<div class="space-y-1">
			<Checkbox bind:checked={fAllowPrivate} label="Allow private/internal addresses" />
			<p class="text-xs text-surface-600-400">
				Applies when this spec carries its own base URL; with it blank, the linked
				integration's setting governs.
			</p>
		</div>
		{#if !fAllowPrivate}
			<Alert tone="neutral">
				Calls resolving to RFC-1918, loopback or link-local addresses will be blocked (SSRF
				guard). On-prem network gear normally needs this <em>enabled</em>.
			</Alert>
		{/if}
	</div>
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>
