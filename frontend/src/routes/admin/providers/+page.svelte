<script lang="ts">
	import { page } from '$app/state';
	import {
		listProviders,
		createProvider,
		updateProvider,
		deleteProvider,
		type Provider
	} from '$lib/api/providers.api';
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
	import { Plus, Pencil, Trash2 } from 'lucide-svelte';
	import SectionNav from '$lib/components/layout/SectionNav.svelte';

	const typeOptions = [
		{ value: 'openai', label: 'OpenAI' },
		{ value: 'anthropic', label: 'Anthropic' },
		{ value: 'gemini', label: 'Google Gemini' },
		{ value: 'deepseek', label: 'DeepSeek' },
		{ value: 'kimi', label: 'Kimi (Moonshot)' },
		{ value: 'ollama', label: 'Ollama' },
		{ value: 'custom', label: 'Custom (OpenAI-compatible)' }
	];

	// What the backend calls when Base URL is left blank, and a model that type
	// actually serves. Shown as hints so a new provider can be filled in without
	// leaving the page to look either one up. `custom` has no default — it is the
	// one type whose base URL the backend requires.
	const typeInfo: Record<string, { baseUrl: string; model: string }> = {
		openai: { baseUrl: 'https://api.openai.com', model: 'gpt-4o' },
		anthropic: { baseUrl: 'https://api.anthropic.com', model: 'claude-opus-5' },
		gemini: { baseUrl: 'https://generativelanguage.googleapis.com', model: 'gemini-2.5-pro' },
		deepseek: { baseUrl: 'https://api.deepseek.com', model: 'deepseek-chat' },
		kimi: { baseUrl: 'https://api.moonshot.ai', model: 'kimi-k2-0905-preview' },
		ollama: { baseUrl: 'http://localhost:11434', model: 'llama3.1' },
		custom: { baseUrl: '', model: '' }
	};

	let items = $state<Provider[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<Provider | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fType = $state('openai');
	let fBaseUrl = $state('');
	let fModel = $state('');
	let fApiKey = $state('');
	let fEnabled = $state(true);
	// Edited as text, not as an object: an admin fixing a typo needs to see what
	// they typed, and re-serialising a parsed object on every keystroke reformats
	// the field under the cursor.
	let fConfig = $state('{}');

	const baseUrlRequired = $derived(fType === 'custom');

	// Parsed on every change so the error shows while typing rather than on save.
	const configError = $derived.by(() => {
		const text = fConfig.trim();
		if (text === '') return '';
		let parsed: unknown;
		try {
			parsed = JSON.parse(text);
		} catch {
			return 'Not valid JSON.';
		}
		if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed))
			return 'Config must be a JSON object.';
		const obj = parsed as Record<string, unknown>;
		const models = obj.models;
		if (models !== undefined) {
			if (!Array.isArray(models) || models.some((m) => typeof m !== 'string' || !m.trim()))
				return '"models" must be an array of model ids.';
		}
		// The workspace id is an opaque `wrkspc_…` handle that only appears in the
		// Console URL, and the two things people reach for instead — the workspace
		// name and the whole URL — are both accepted by the form, saved, and then
		// rejected by Anthropic one chat turn later with "must be a valid workspace
		// ID". Naming the mistake here is the difference between a five-second fix
		// and a hunt through the logs.
		const workspace = obj.workspace_id;
		if (workspace !== undefined) {
			if (typeof workspace !== 'string' || !workspace.trim())
				return '"workspace_id" must be a non-empty workspace id.';
			const value = workspace.trim();
			if (!WORKSPACE_ID.test(value)) {
				// An id buried in a longer string is a pasted URL; anything else —
				// including a bare `wrkspc_` — is someone who does not have the id yet.
				const embedded = value.match(WORKSPACE_ID_ANYWHERE);
				return embedded
					? `"workspace_id" looks like a URL — paste only ${embedded[0]}.`
					: '"workspace_id" is the id, not the workspace name. Open the workspace in the Anthropic Console; the id is the wrkspc_… segment of the URL.';
			}
		}
		return '';
	});

	function parsedConfig(): Record<string, unknown> {
		const text = fConfig.trim();
		return text === '' ? {} : (JSON.parse(text) as Record<string, unknown>);
	}

	// Console workspace handles are `wrkspc_` followed by an opaque token.
	const WORKSPACE_ID = /^wrkspc_[A-Za-z0-9_-]+$/;
	const WORKSPACE_ID_ANYWHERE = /wrkspc_[A-Za-z0-9_-]+/;

	const configHint = $derived(
		'Optional. "models" lists extra model ids this provider serves — they join the ' +
			'default model in the chat model picker. "model_limits" sets context_window ' +
			'and max_output_tokens.' +
			// Only for Anthropic: an identity-linked key belongs to a person with access
			// to several workspaces, and the API rejects the request until one is named.
			(fType === 'anthropic'
				? ' "workspace_id" (the wrkspc_… id from the Console URL, not the workspace ' +
					'name) is required when the API key is identity-linked. A key created ' +
					'inside a workspace carries its own and needs none.'
				: '') +
			' Other keys are stored untouched.'
	);
	const baseUrlHint = $derived(
		baseUrlRequired
			? 'Required — the OpenAI-compatible endpoint to call, e.g. https://my-gateway.internal'
			: `Optional; defaults to ${typeInfo[fType]?.baseUrl ?? 'the vendor endpoint'}`
	);
	const modelHint = $derived(
		typeInfo[fType]?.model ? `e.g. ${typeInfo[fType].model}` : 'the model id the endpoint expects'
	);

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'type', header: 'Type' },
		{ key: 'defaultModel', header: 'Default model' },
		{ key: 'status', header: 'Status' },
		{ key: 'actions', header: '', align: 'right' }
	];

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listProviders()).items;
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
		fName = '';
		fType = 'openai';
		fBaseUrl = '';
		fModel = '';
		fApiKey = '';
		fEnabled = true;
		fConfig = JSON.stringify({ models: [] }, null, 2);
		modalOpen = true;
	}

	function openEdit(p: Provider) {
		editing = p;
		formError = '';
		fName = p.name;
		fType = p.type;
		fBaseUrl = p.baseUrl ?? '';
		fModel = p.defaultModel;
		fApiKey = '';
		fEnabled = p.enabled;
		fConfig = Object.keys(p.config).length === 0 ? '{}' : JSON.stringify(p.config, null, 2);
		modalOpen = true;
	}

	async function save() {
		if (!fName.trim() || !fModel.trim()) {
			formError = 'Name and default model are required.';
			return;
		}
		if (baseUrlRequired && !fBaseUrl.trim()) {
			formError = 'A custom provider needs the base URL of its OpenAI-compatible endpoint.';
			return;
		}
		if (configError) {
			formError = `Config: ${configError}`;
			return;
		}
		formError = '';
		saving = true;
		try {
			if (editing) {
				await updateProvider(editing.id, {
					name: fName.trim(),
					baseUrl: fBaseUrl.trim(),
					apiKey: fApiKey,
					defaultModel: fModel.trim(),
					enabled: fEnabled,
					config: parsedConfig()
				});
			} else {
				await createProvider({
					name: fName.trim(),
					type: fType,
					baseUrl: fBaseUrl.trim(),
					apiKey: fApiKey,
					defaultModel: fModel.trim(),
					enabled: fEnabled,
					config: parsedConfig()
				});
			}
			modalOpen = false;
			toast.success(editing ? 'Provider updated' : 'Provider created');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the provider');
		} finally {
			saving = false;
		}
	}

	async function remove(p: Provider) {
		const ok = await confirm({
			title: 'Delete provider?',
			message: `"${p.name}" will be removed.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteProvider(p.id);
			items = items.filter((x) => x.id !== p.id);
			toast.success('Provider deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the provider");
		}
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>AI Providers · Nashira</title></svelte:head>

<SectionNav id="ai-studio" />

<PageHeader title="AI Providers" description="LLM providers for the agent.">
	{#snippet actions()}
		<Button variant="primary" onclick={openCreate}><Plus size={15} />New provider</Button>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<DataTable {loading} {columns} rows={items} rowKey={(p) => p.id} empty="No providers configured — the agent cannot answer until one model endpoint exists.">
		{#snippet cell(row, col)}
			{#if col.key === 'name'}
				<span class="font-medium">{row.name}</span>
			{:else if col.key === 'type'}
				<Badge>{row.type}</Badge>
			{:else if col.key === 'defaultModel'}
				<code class="text-xs text-surface-600-400">{row.defaultModel}</code>
			{:else if col.key === 'status'}
				<StatusBadge status={row.enabled ? 'enabled' : 'disabled'} />
			{:else if col.key === 'actions'}
				<div class="flex justify-end gap-1">
					<IconButton label="Edit provider" onclick={() => openEdit(row)}><Pencil size={14} /></IconButton>
					<IconButton label="Delete provider" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
				</div>
			{/if}
		{/snippet}
	</DataTable>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit provider' : 'New provider'}>
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}
		<div class="grid gap-3 sm:grid-cols-2">
			<Input label="Name" bind:value={fName} required />
			<Select label="Type" bind:value={fType} options={typeOptions} disabled={!!editing} />
		</div>
		<Input label="Default model" bind:value={fModel} required hint={modelHint} />
		<Input label="Base URL" bind:value={fBaseUrl} required={baseUrlRequired} hint={baseUrlHint} />
		<Input
			label="API key"
			bind:value={fApiKey}
			type="password"
			hint={editing
				? 'Leave blank to keep the stored key'
				: 'Stored encrypted; never shown again'}
		/>
		<Textarea
			label="Config (JSON)"
			bind:value={fConfig}
			rows={7}
			spellcheck="false"
			error={configError}
			hint={configHint}
		/>
		<Checkbox bind:checked={fEnabled} label="Enabled" />
	</div>
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>
