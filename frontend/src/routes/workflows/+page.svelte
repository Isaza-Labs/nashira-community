<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import {
		listWorkflows,
		downloadYaml,
		importWorkflow,
		deleteWorkflow,
		type WorkflowSummary
	} from '$lib/api/workflows.api';
	import { timeAgo } from '$lib/utils/time';
	import {
		PageHeader,
		Badge,
		Button,
		Checkbox,
		Tabs,
		IconButton,
		Modal,
		Input,
		CodeEditor,
		Alert,
		Spinner,
		ErrorState,
		confirm,
		toast
	} from '$lib/components/ui';
	import type { Tone } from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { Download, Upload, Trash2 } from 'lucide-svelte';

	// One list per environment, as in the promotion path itself: a draft, its QA copy
	// and its production copy are three rows with the same name, and a flat list mixes
	// them so the name stops identifying anything. The filter is server-side so the
	// tab's count describes the same set as the rows under it.
	const ENVIRONMENTS = ['draft', 'qa', 'production'] as const;
	type Environment = (typeof ENVIRONMENTS)[number];

	function readEnv(raw: string | null): Environment {
		return ENVIRONMENTS.includes(raw as Environment) ? (raw as Environment) : 'draft';
	}

	let environment = $state<Environment>(readEnv(page.url.searchParams.get('env')));
	// Per-environment totals, so an empty Production tab is distinguishable from a
	// filter that is not working before anyone clicks it.
	let counts = $state<Record<Environment, number | null>>({ draft: null, qa: null, production: null });

	let items = $state<WorkflowSummary[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	// Per-row export state, so one download's spinner doesn't disable the others.
	let exportingId = $state<string | null>(null);
	let deletingId = $state<string | null>(null);

	// Bulk selection. Held as a plain array of ids rather than a Set so assigning it
	// is what triggers reactivity, and filtered against `items` on every read so a
	// workflow deleted individually cannot linger in the selection.
	let selectedIds = $state<string[]>([]);
	let bulkDeleting = $state(false);

	const selected = $derived(items.filter((w) => selectedIds.includes(w.id)));
	const allSelected = $derived(items.length > 0 && selected.length === items.length);

	function toggleOne(id: string, on: boolean) {
		selectedIds = on ? [...selectedIds, id] : selectedIds.filter((x) => x !== id);
	}

	function toggleAll(on: boolean) {
		selectedIds = on ? items.map((w) => w.id) : [];
	}

	let importOpen = $state(false);
	let importing = $state(false);
	let importContent = $state('');
	let importName = $state('');
	let importError = $state('');

	let seq = 0;

	async function load() {
		const mine = ++seq;
		loading = true;
		error = null;
		try {
			const res = await listWorkflows(environment, 100, 0);
			// A slower earlier tab must not paint over the one now selected.
			if (mine !== seq) return;
			items = res.items;
			counts = { ...counts, [environment]: res.total };
		} catch (e) {
			if (mine !== seq) return;
			error = e;
		} finally {
			if (mine === seq) loading = false;
		}
	}

	// The other two tabs' totals. Cheap (limit=1, the server answers with the count)
	// and refreshed after anything that changes how many workflows exist.
	async function loadCounts() {
		const results = await Promise.all(
			ENVIRONMENTS.map((env) =>
				listWorkflows(env, 1, 0)
					.then((r) => r.total)
					.catch(() => null)
			)
		);
		counts = { draft: results[0], qa: results[1], production: results[2] };
	}

	$effect(() => {
		environment;
		selectedIds = [];
		load();
	});

	$effect(() => {
		loadCounts();
	});

	function switchEnvironment(next: string) {
		environment = readEnv(next);
		// Keep the tab in the URL so a reload, a bookmark or a shared link lands on
		// the same environment rather than silently back on Draft.
		const url = new URL(page.url);
		if (environment === 'draft') url.searchParams.delete('env');
		else url.searchParams.set('env', environment);
		goto(`${url.pathname}${url.search}`, { replaceState: true, noScroll: true, keepFocus: true });
	}

	const envTabs = $derived(
		ENVIRONMENTS.map((env) => ({
			value: env,
			label:
				(env === 'qa' ? 'QA' : env[0].toUpperCase() + env.slice(1)) +
				(counts[env] === null ? '' : ` (${counts[env]})`)
		}))
	);

	function fmt(iso: string): string {
		return timeAgo(iso);
	}

	function envTone(env: string): Tone {
		if (env === 'production') return 'success';
		if (env === 'qa') return 'primary';
		return 'neutral';
	}

	async function exportOne(w: WorkflowSummary) {
		exportingId = w.id;
		try {
			await downloadYaml(w.id, w.name, w.version, w.environment);
		} catch (e) {
			toast.fromError(e, "Couldn't export the workflow");
		} finally {
			exportingId = null;
		}
	}

	// The environment is in the question because it is the whole risk: the API deletes a
	// promoted copy as readily as a draft, and "Delete workflow?" alone does not tell
	// someone they are about to remove the production one.
	async function removeOne(w: WorkflowSummary) {
		const ok = await confirm({
			title: `Delete "${w.name}"?`,
			message:
				`This removes the ${w.environment} copy at v${w.version}. Its past runs and audit ` +
				`trail stay readable, and any promoted copy in another environment is untouched.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;

		deletingId = w.id;
		try {
			await deleteWorkflow(w.id);
			items = items.filter((x) => x.id !== w.id);
			selectedIds = selectedIds.filter((x) => x !== w.id);
			counts = { ...counts, [w.environment as Environment]: (counts[w.environment as Environment] ?? 1) - 1 };
			toast.success(`Deleted "${w.name}"`);
		} catch (e) {
			toast.fromError(e, "Couldn't delete the workflow");
		} finally {
			deletingId = null;
		}
	}

	// Same risk as the single-row delete, multiplied — so the question names the
	// promoted copies explicitly instead of only counting rows. Selecting eleven
	// drafts and one production workflow looks identical at a glance otherwise.
	async function removeSelected() {
		const victims = selected;
		if (victims.length === 0) return;

		const promoted = victims.filter((w) => w.environment !== 'draft');
		const promotedNote = promoted.length
			? ` ${promoted.length} of them ${promoted.length === 1 ? 'is' : 'are'} promoted: ` +
				`${promoted.map((w) => `${w.name} (${w.environment})`).join(', ')}.`
			: '';

		const ok = await confirm({
			title: `Delete ${victims.length} workflow${victims.length === 1 ? '' : 's'}?`,
			message:
				`This removes the selected copies at their current version.${promotedNote} ` +
				`Past runs and audit trails stay readable, and copies in other environments are untouched.`,
			tone: 'danger',
			confirmLabel: `Delete ${victims.length}`
		});
		if (!ok) return;

		bulkDeleting = true;
		// Sequential on purpose: every delete is audited and the API is the same one a
		// single row calls, so firing twelve at once buys nothing and makes a partial
		// failure harder to report. Failures are collected rather than aborting the rest.
		const failed: string[] = [];
		for (const w of victims) {
			try {
				await deleteWorkflow(w.id);
				items = items.filter((x) => x.id !== w.id);
				selectedIds = selectedIds.filter((x) => x !== w.id);
			} catch {
				failed.push(w.name);
			}
		}
		bulkDeleting = false;

		const done = victims.length - failed.length;
		if (done > 0) {
			toast.success(`Deleted ${done} workflow${done === 1 ? '' : 's'}`);
			await loadCounts();
		}
		if (failed.length > 0) toast.error(`Couldn't delete: ${failed.join(', ')}`);
	}

	function openImport() {
		importContent = '';
		importName = '';
		importError = '';
		importOpen = true;
	}

	// Loading a file seeds an empty name override from the filename, mirroring the
	// skill/spec upload flow.
	function onImportFile(filename: string) {
		if (!importName.trim()) importName = filename.replace(/\.(ya?ml|json)$/i, '');
	}

	async function runImport() {
		if (!importContent.trim()) {
			importError = 'Paste or load a workflow document first.';
			return;
		}
		importError = '';
		importing = true;
		try {
			const created = await importWorkflow(importContent, importName, 'imported');
			importOpen = false;
			toast.success(`Imported "${created.name}" as a draft`);
			await goto(`/workflows/${created.id}`);
		} catch (e) {
			toast.fromError(e, 'Failed to import the workflow');
		} finally {
			importing = false;
		}
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !importOpen) openImport();
	});
</script>

<svelte:head><title>Workflows · Nashira</title></svelte:head>

<PageHeader
	title="Workflows"
	description="Review a workflow's execution plan and rollback safety, then run it with confirmation."
>
	{#snippet actions()}
		<RoleGate require="operator">
			<Button variant="secondary" onclick={openImport}><Upload size={15} />Import</Button>
		</RoleGate>
	{/snippet}
</PageHeader>

<Tabs tabs={envTabs} value={environment} onchange={switchEnvironment} />

<div class="pt-4"></div>

{#if loading}
	<div class="flex justify-center py-16"><Spinner size="lg" /></div>
{:else if error}
	<ErrorState {error} onRetry={load} />
{:else if items.length === 0}
	<div
		class="rounded-xl border border-surface-200-800 px-4 py-16 text-center text-sm text-surface-600-400"
	>
		{#if environment === 'draft'}
			No drafts. Import one, or ask the agent to build it.
		{:else}
			Nothing in {environment === 'qa' ? 'QA' : 'production'} yet. A workflow gets here by being
			promoted from {environment === 'qa' ? 'a draft' : 'QA'} — it is never created here directly.
		{/if}
	</div>
{:else}
	<RoleGate require="operator">
		<div class="mb-3 flex min-h-9 items-center gap-3">
			<Checkbox
				bind:checked={() => allSelected, (on) => toggleAll(on)}
				label={allSelected ? 'Clear selection' : 'Select all'}
				disabled={bulkDeleting}
			/>
			{#if selected.length > 0}
				<span class="text-sm text-surface-600-400">{selected.length} selected</span>
				<Button variant="danger" size="sm" loading={bulkDeleting} onclick={removeSelected}>
					<Trash2 size={14} />Delete selected
				</Button>
			{/if}
		</div>
	</RoleGate>

	<div class="overflow-hidden rounded-xl border border-surface-200-800">
		{#each items as w (w.id)}
			<!-- The row controls are siblings of the link, not children: a <button>
			     inside an <a> is invalid and swallows the row's own click. The same
			     applies to the checkbox, which is a <label> wrapping an <input>. -->
			<div
				class="flex items-center border-b border-surface-100-900 pr-2 pl-4 transition last:border-0 hover:bg-surface-100-900/60"
			>
				<RoleGate require="operator">
					<div class="pr-3">
						<Checkbox
							bind:checked={
								() => selectedIds.includes(w.id), (on) => toggleOne(w.id, on)
							}
							ariaLabel={`Select ${w.name}`}
							disabled={bulkDeleting}
						/>
					</div>
				</RoleGate>
				<a href={`/workflows/${w.id}`} class="flex min-w-0 flex-1 items-center gap-3 py-3 pr-3">
					<div class="min-w-0 flex-1">
						<div class="truncate font-medium">{w.name}</div>
						<div class="text-xs text-surface-600-400">v{w.version}, updated {fmt(w.updatedAt)}</div>
					</div>
					{#if w.environment !== environment}
						<!-- Only when it contradicts the tab, which would mean the row came
						     from somewhere other than this filter. Repeating the tab's own
						     name on every row is noise. -->
						<Badge tone={envTone(w.environment)}>{w.environment}</Badge>
					{/if}
				</a>
				<div class="flex items-center gap-1">
					<IconButton
						label={`Export ${w.name} as YAML`}
						disabled={exportingId === w.id}
						onclick={() => exportOne(w)}
					>
						{#if exportingId === w.id}<Spinner size="sm" />{:else}<Download size={14} />{/if}
					</IconButton>
					<RoleGate require="operator">
						<IconButton
							label={`Delete ${w.name}`}
							disabled={deletingId === w.id}
							onclick={() => removeOne(w)}
						>
							{#if deletingId === w.id}<Spinner size="sm" />{:else}<Trash2 size={14} />{/if}
						</IconButton>
					</RoleGate>
				</div>
			</div>
		{/each}
	</div>
{/if}

<Modal bind:open={importOpen} title="Import workflow">
	<div class="space-y-3">
		{#if importError}<Alert tone="error">{importError}</Alert>{/if}
		<Alert tone="neutral">
			The workflow is imported as a new <strong>draft</strong>. Any id or environment in the file is
			ignored, so an import can never overwrite an existing workflow or skip the promotion gate.
		</Alert>
		<CodeEditor
			label="Workflow YAML"
			language="yaml"
			bind:value={importContent}
			accept=".yaml,.yml,.json"
			onfile={onImportFile}
			rows={14}
			hint="Paste the document, or load a file exported from any Nashira instance."
		/>
		<Input
			label="Name"
			bind:value={importName}
			hint="Optional. Leave blank to keep the name in the document."
		/>
	</div>
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (importOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={importing} onclick={runImport}>Import</Button>
	{/snippet}
</Modal>
