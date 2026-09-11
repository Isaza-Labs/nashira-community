<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import {
		listSkills,
		listBuiltinSkills,
		saveBuiltinSkill,
		createSkill,
		updateSkill,
		deleteSkill,
		importSkills,
		type Skill,
		type BuiltinSkill,
		type ImportSkillsSummary
	} from '$lib/api/skills.api';
	import { listIntegrations, type Integration } from '$lib/api/integrations.api';
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
		CodeEditor,
		Checkbox,
		Alert,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import { Plus, Pencil, Trash2, ExternalLink, Upload } from 'lucide-svelte';
	import FilterChip from '$lib/components/layout/FilterChip.svelte';
	import SectionNav from '$lib/components/layout/SectionNav.svelte';

	// One table shows both sources: built-in file skills (read-only, shipped with
	// the backend, always first in the prompt) and the tenant's uploaded rows.
	type Row = {
		id: string;
		name: string;
		priority: number | null; // null = built-in (file order, no priority)
		isActive: boolean;
		builtin: boolean;
		skill: Skill | null; // the editable row, when not built-in
		content: string;
	};

	let items = $state<Skill[]>([]);
	let builtins = $state<BuiltinSkill[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	// Built-in file editing: separate modal (name is fixed, no priority/active).
	let builtinOpen = $state(false);
	let builtinName = $state('');
	let builtinContent = $state('');
	let builtinSaving = $state(false);

	const rows = $derived<Row[]>([
		...builtins.map((b) => ({
			id: `builtin:${b.name}`,
			name: b.name,
			priority: null,
			isActive: true,
			builtin: true,
			skill: null,
			content: b.content
		})),
		...items.map((s) => ({
			id: s.id,
			name: s.name,
			priority: s.priority,
			isActive: s.isActive,
			builtin: false,
			skill: s,
			content: s.content
		}))
	]);

	let modalOpen = $state(false);
	let editing = $state<Skill | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fContent = $state('');
	let fPriority = $state('0');
	let fActive = $state(true);
	let fIntegrationId = $state('');

	// A skill scoped to an integration tells the agent that the operations it
	// describes target that integration's URL and credentials.
	let integrations = $state<Integration[]>([]);
	const integrationOptions = $derived([
		{ value: '', label: '— global (every integration) —' },
		...integrations.map((i) => ({ value: i.id, label: i.name }))
	]);

	// Arriving from an integration shows the skills scoped to it. Built-ins are
	// global by construction, so a scoped view drops them rather than implying
	// they belong to the integration.
	const filterId = $derived(page.url.searchParams.get('integration'));
	const filterIntegration = $derived(integrations.find((i) => i.id === filterId) ?? null);
	// The narrowing happens server-side now (see load): unfiltered, this page asks
	// for global skills only, so a skill that belongs to an integration is not
	// something you can stumble into and edit from a page that never says which
	// system it applies to. Built-ins are global by construction, so a scoped view
	// drops them rather than implying they belong to the integration.
	const visibleRows = $derived(filterId ? rows.filter((r) => !!r.skill?.integrationId) : rows);

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'priority', header: 'Priority' },
		{ key: 'status', header: 'Status' },
		{ key: 'actions', header: '', align: 'right' }
	];

	async function load() {
		loading = true;
		error = null;
		try {
			const [db, files] = await Promise.all([
				listSkills(100, 0, { integrationId: filterId }),
				listBuiltinSkills()
			]);
			items = db.items;
			builtins = files;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	function openBuiltin(r: Row) {
		builtinName = r.name;
		builtinContent = r.content;
		builtinOpen = true;
	}

	async function saveBuiltin() {
		if (!builtinContent.trim()) {
			toast.warning('Content cannot be empty.');
			return;
		}
		builtinSaving = true;
		try {
			await saveBuiltinSkill(builtinName, builtinContent);
			builtinOpen = false;
			toast.success(`${builtinName} saved`);
			await load();
		} catch (e) {
			toast.fromError(e, "Couldn't save the built-in skill");
		} finally {
			builtinSaving = false;
		}
	}

	$effect(() => {
		// Read explicitly: the scope decides what load() asks the server for, and a
		// dependency that only exists inside a Promise.all argument is one refactor
		// away from silently not re-running.
		filterId;
		load();
		listIntegrations()
			.then((r) => (integrations = r.items))
			.catch(() => (integrations = []));
	});

	function openCreate() {
		editing = null;
		formError = '';
		fName = '';
		fContent = '';
		fPriority = '0';
		// Default to the scope being viewed. Left at global, pressing New on a
		// filtered page and saving sends the admin straight back out to the global
		// list — which reads as the app having un-scoped what they just made.
		fIntegrationId = filterId ?? '';
		fActive = true;
		modalOpen = true;
	}

	// Loading a file fills an empty name from the filename, so uploading a skill
	// is a single action. An explicit name the user already typed is kept.
	function onContentFile(filename: string) {
		if (!fName.trim()) fName = filename.replace(/\.(md|markdown|txt)$/i, '');
	}

	function openEdit(s: Skill) {
		editing = s;
		formError = '';
		fName = s.name;
		fContent = s.content;
		fPriority = String(s.priority);
		fIntegrationId = s.integrationId ?? '';
		fActive = s.isActive;
		modalOpen = true;
	}

	async function save() {
		if (!fName.trim()) {
			formError = 'Name is required.';
			return;
		}
		const priority = Number.parseInt(fPriority, 10) || 0;
		saving = true;
		try {
			const integrationId = fIntegrationId;
			if (editing) {
				await updateSkill(editing.id, {
					name: fName.trim(),
					content: fContent,
					priority,
					integrationId,
					isActive: fActive
				});
			} else {
				await createSkill({ name: fName.trim(), content: fContent, priority, integrationId });
			}
			modalOpen = false;
			// This list is global-only, so a skill that was just scoped to an
			// integration is no longer in it. Following the row is the difference
			// between "saved" and "saved, and here it is" — leaving the user on a list
			// it vanished from is how a successful save reads as a failed one.
			// Compare SCOPES, not "is a scope set": clearing the scope on a filtered
			// page moves the row to the global list, which is just as invisible from
			// here as the other direction.
			const newScope = integrationId || null;
			const listScope = filterId || null;
			if (newScope !== listScope) {
				const owner = integrations.find((i) => i.id === newScope);
				toast.success(editing ? 'Skill updated' : 'Skill created', {
					description: newScope
						? `Scoped to ${owner?.name ?? 'an integration'} — showing its skills.`
						: 'Now global — showing the global skills.'
				});
				await goto(newScope ? `/admin/skills?integration=${newScope}` : '/admin/skills');
				return;
			}
			toast.success(editing ? 'Skill updated' : 'Skill created');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the skill');
		} finally {
			saving = false;
		}
	}

	async function remove(s: Skill) {
		const ok = await confirm({
			title: 'Delete skill?',
			message: `"${s.name}" will be removed from the system prompt.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteSkill(s.id);
			items = items.filter((x) => x.id !== s.id);
			toast.success('Skill deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the skill");
		}
	}

	// ── bulk import ─────────────────────────────────────────────────────────
	// Skills are written as markdown files and shared as a directory of them, so the
	// import takes a multi-select and reports one verdict per file. The first pass
	// never overwrites: an import that silently replaced a hand-tuned prompt would be
	// discovered days later, in the agent's behaviour. Anything that already exists
	// comes back as "skipped" with a button to replace it deliberately.
	let importInput = $state<HTMLInputElement | undefined>(undefined);
	let importing = $state(false);
	let importSummary = $state<ImportSkillsSummary | null>(null);
	let importResultOpen = $state(false);
	let pendingFiles = $state<{ name: string; content: string }[]>([]);

	async function onImportPick(e: Event) {
		const input = e.target as HTMLInputElement;
		const picked = Array.from(input.files ?? []);
		input.value = ''; // let the same files be picked again after a fix
		if (picked.length === 0) return;

		try {
			pendingFiles = await Promise.all(
				picked.map(async (f) => ({ name: f.name, content: await f.text() }))
			);
		} catch {
			toast.error("Couldn't read those files");
			return;
		}
		await runImport(false);
	}

	async function runImport(overwrite: boolean) {
		if (pendingFiles.length === 0) return;
		importing = true;
		try {
			importSummary = await importSkills(pendingFiles, { overwrite });
			importResultOpen = true;
			if (importSummary.created + importSummary.updated > 0) await load();
		} catch (err) {
			toast.fromError(err, "Couldn't import those skills");
		} finally {
			importing = false;
		}
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Prompt Skills · Nashira</title></svelte:head>

<SectionNav id="ai-studio" />

<PageHeader title="Prompt Skills" description="Composable system-prompt fragments.">
	{#snippet actions()}
		<input
			bind:this={importInput}
			type="file"
			accept=".md,.markdown,text/markdown"
			multiple
			class="hidden"
			onchange={onImportPick}
		/>
		<Button variant="secondary" loading={importing} onclick={() => importInput?.click()}>
			<Upload size={15} />Import .md
		</Button>
		<Button variant="primary" onclick={openCreate}><Plus size={15} />New skill</Button>
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
					clearHref="/admin/skills"
					count={visibleRows.length}
				/>
				<a
					class="inline-flex items-center gap-1 text-xs text-surface-600-400 underline hover:text-primary-700-300"
					href={`/admin/specs?integration=${filterId}`}
				>
					Its API specs<ExternalLink size={11} />
				</a>
			</div>
		{/if}

		<DataTable
			{loading}
			{columns}
			rows={visibleRows}
			rowKey={(r) => r.id}
			empty={filterId
				? 'No skill is scoped to this integration yet — its specs describe what to call, a skill describes how to operate it.'
				: 'No global skills. Skills that belong to an integration live on that integration.'}
		>
			{#snippet cell(row, col)}
				{#if col.key === 'name'}
					<div class="flex items-center gap-2">
						<span class="font-medium">{row.name}</span>
						{#if row.builtin}<Badge>built-in</Badge>{/if}
					</div>
				{:else if col.key === 'priority'}
					{#if row.builtin}
						<span class="text-surface-600-400">—</span>
					{:else}
						<span class="tabular-nums text-surface-600-400">{row.priority}</span>
					{/if}
				{:else if col.key === 'status'}
					<StatusBadge status={row.isActive ? 'active' : 'disabled'} />
				{:else if col.key === 'actions'}
					<div class="flex justify-end gap-1">
						{#if row.builtin}
							<IconButton label="Edit built-in skill" onclick={() => openBuiltin(row)}>
								<Pencil size={14} />
							</IconButton>
						{:else if row.skill}
							{@const s = row.skill}
							<IconButton label="Edit skill" onclick={() => openEdit(s)}><Pencil size={14} /></IconButton>
							<IconButton label="Delete skill" onclick={() => remove(s)}><Trash2 size={14} /></IconButton>
						{/if}
					</div>
				{/if}
			{/snippet}
		</DataTable>
	</div>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit skill' : 'New skill'} size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}
		<div class="grid gap-3 sm:grid-cols-2">
			<Input label="Name" bind:value={fName} required />
			<Input label="Priority" bind:value={fPriority} type="number" hint="Lower is included first; base.md should stay near 0" />
		</div>
		<Select label="Integration scope" bind:value={fIntegrationId} options={integrationOptions} />
		<CodeEditor
			label="Content"
			bind:value={fContent}
			language="markdown"
			rows={14}
			accept=".md,.markdown,.txt"
			onfile={onContentFile}
			hint="Prompt text (markdown) — paste it or load a .md file"
		/>
		{#if editing}<Checkbox bind:checked={fActive} label="Active" />{/if}
	</div>
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>

<!-- Editor for built-in file skills. Name is fixed; the content is validated
     server-side like any uploaded skill and hot-reloads the agent prompt. -->
<Modal bind:open={builtinOpen} title={builtinName} size="lg">
	<CodeEditor
		bind:value={builtinContent}
		language="markdown"
		rows={18}
		accept=".md,.markdown,.txt"
		hint="Built-in file — shipped with the backend; a redeploy restores the original unless the Skills directory is persisted"
	/>
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (builtinOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={builtinSaving} onclick={saveBuiltin}>Save</Button>
	{/snippet}
</Modal>

<!-- Import verdict, one row per file. A bulk result that is only a count sends the
     caller back to re-upload everything to find out which file was refused. -->
<Modal bind:open={importResultOpen} title="Import results" size="lg">
	{#if importSummary}
		<div class="space-y-3 text-sm">
			<div class="flex flex-wrap gap-1.5">
				<Badge tone="success">{importSummary.created} created</Badge>
				<Badge>{importSummary.updated} updated</Badge>
				{#if importSummary.skipped > 0}<Badge tone="warning">{importSummary.skipped} skipped</Badge>{/if}
				{#if importSummary.failed > 0}<Badge tone="error">{importSummary.failed} failed</Badge>{/if}
			</div>

			<div class="max-h-72 space-y-1 overflow-auto">
				{#each importSummary.results as r (r.name)}
					<div class="flex items-start gap-2 rounded-md bg-surface-100-900 px-2 py-1.5">
						<Badge
							tone={r.status === 'created' || r.status === 'updated'
								? 'success'
								: r.status === 'skipped'
									? 'warning'
									: 'error'}>{r.status}</Badge
						>
						<div class="min-w-0 flex-1">
							<div class="truncate font-medium">{r.name}</div>
							{#if r.error}<div class="text-xs text-surface-600-400">{r.error}</div>{/if}
						</div>
					</div>
				{/each}
			</div>

			{#if importSummary.skipped > 0}
				<Alert tone="warning" title="Some skills already exist">
					Replacing them overwrites the stored content — including any edits made here.
				</Alert>
			{/if}
		</div>
	{/if}
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (importResultOpen = false)}>Close</Button>
		{#if importSummary && importSummary.skipped > 0}
			<Button variant="primary" loading={importing} onclick={() => runImport(true)}>
				Replace {importSummary.skipped} existing
			</Button>
		{/if}
	{/snippet}
</Modal>
