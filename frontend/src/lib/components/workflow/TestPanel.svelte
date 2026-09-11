<script lang="ts">
	import {
		listTests,
		createTest,
		deleteTest,
		runTest,
		listVersions,
		restoreVersion,
		type AcceptanceTest,
		type WorkflowVersionSummary
	} from '$lib/api/triggers.api';
	import {
		Button,
		Modal,
		Badge,
		IconButton,
		Input,
		Textarea,
		CodeEditor,
		Alert,
		Spinner,
		ErrorState,
		confirm,
		toast,
		type Tone
	} from '$lib/components/ui';
	import JsonSchemaForm from '$lib/components/JsonSchemaForm.svelte';
	import DevicePicker from '$lib/components/device/DevicePicker.svelte';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { effectiveInputSchema, hasInputFields } from '$lib/workflow/deriveInputs';
	import { buildSchemaDefaults } from '$lib/workflow/schemaDefaults';
	import type { Workflow } from '$lib/api/workflows.api';
	import { goto } from '$app/navigation';
	import { Plus, Trash2, Play, RotateCcw } from 'lucide-svelte';

	// Acceptance tests and promoted versions for one workflow. Both answer
	// "is this safe to promote" from different directions, so they share a panel.
	// A test is a real run, so it is configured with the same input form and the
	// same device picker a manual run uses.
	let {
		workflowId,
		workflow,
		mode
	}: { workflowId: string; workflow: Workflow; mode: 'tests' | 'versions' } = $props();

	const schema = $derived(effectiveInputSchema(workflow));
	const showInputs = $derived(hasInputFields(schema));

	let tests = $state<AcceptanceTest[]>([]);
	let versions = $state<WorkflowVersionSummary[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);
	let runningId = $state<string | null>(null);

	let modalOpen = $state(false);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fDescription = $state('');
	let fInput = $state<Record<string, unknown>>({});
	let fAssertions = $state('');
	let fTargets = $state<string[]>([]);

	function statusTone(s: string | null): Tone {
		if (s === 'passed') return 'success';
		if (s === 'failed') return 'error';
		if (s === 'error') return 'warning';
		return 'neutral';
	}

	function fmt(iso: string | null): string {
		return iso ? new Date(iso).toLocaleString() : '—';
	}

	async function load() {
		loading = true;
		error = null;
		try {
			if (mode === 'tests') tests = (await listTests(workflowId)).items;
			else versions = await listVersions(workflowId);
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
		formError = '';
		fName = '';
		fDescription = '';
		fInput = buildSchemaDefaults(schema);
		fAssertions = '[\n  { "kind": "status_equals", "expected": "completed" }\n]';
		fTargets = [];
		modalOpen = true;
	}

	async function save() {
		if (!fName.trim()) {
			formError = 'Name is required.';
			return;
		}
		formError = '';
		saving = true;
		try {
			await createTest(workflowId, {
				name: fName.trim(),
				description: fDescription,
				input: fInput,
				targetDevices: fTargets,
				assertions: fAssertions
			});
			modalOpen = false;
			toast.success('Test created');
			await load();
		} catch (e) {
			// Assertions are still raw JSON — a syntax error can only come from there.
			if (e instanceof SyntaxError) formError = `Assertions: ${e.message}`;
			else toast.fromError(e, 'Failed to save the test');
		} finally {
			saving = false;
		}
	}

	async function run(t: AcceptanceTest) {
		const ok = await confirm({
			title: 'Run acceptance test?',
			message: `"${t.name}" executes the workflow for real against its target devices. A simulation would not.`,
			confirmLabel: 'Run'
		});
		if (!ok) return;

		runningId = t.id;
		try {
			const r = await runTest(workflowId, t.id);
			if (r.status === 'passed') toast.success(`${t.name}: passed`);
			else toast.error(`${t.name}: ${r.status} — ${r.failures[0] ?? 'see details'}`);
			await load();
		} catch (e) {
			toast.fromError(e, "Couldn't run the test");
		} finally {
			runningId = null;
		}
	}

	async function remove(t: AcceptanceTest) {
		const ok = await confirm({
			title: 'Delete test?',
			message: `"${t.name}" will be removed.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteTest(workflowId, t.id);
			tests = tests.filter((x) => x.id !== t.id);
			toast.success('Test deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the test");
		}
	}

	async function restore(v: WorkflowVersionSummary) {
		const ok = await confirm({
			title: `Restore version ${v.version}?`,
			message:
				'This creates a NEW draft from the snapshot — it does not overwrite anything. The draft still has to be simulated and promoted like any other change.',
			confirmLabel: 'Restore as draft'
		});
		if (!ok) return;
		try {
			const created = await restoreVersion(v.id);
			toast.success(`Restored version ${v.version} as a draft`);
			await goto(`/workflows/${created.id}`);
		} catch (e) {
			toast.fromError(e, "Couldn't restore the version");
		}
	}
</script>

{#if loading}
	<div class="flex justify-center py-10"><Spinner /></div>
{:else if error}
	<ErrorState {error} onRetry={load} compact />
{:else if mode === 'tests'}
	<div class="space-y-3">
		<div class="flex items-center justify-between">
			<p class="text-sm text-surface-600-400">
				A simulation proves the graph is sound; only a test run proves the behaviour.
			</p>
			<RoleGate require="operator">
				<Button variant="secondary" size="sm" onclick={openCreate}><Plus size={14} />New test</Button>
			</RoleGate>
		</div>

		{#if tests.length === 0}
			<div class="rounded-xl border border-surface-200-800 px-4 py-10 text-center text-sm text-surface-600-400">
				No acceptance tests.
			</div>
		{:else}
			<div class="space-y-2">
				{#each tests as t (t.id)}
					<div class="rounded-xl border border-surface-200-800 p-3">
						<div class="flex items-start gap-3">
							<div class="min-w-0 flex-1">
								<div class="flex flex-wrap items-center gap-2">
									<span class="font-medium">{t.name}</span>
									{#if t.lastStatus}
										<Badge tone={statusTone(t.lastStatus)}>{t.lastStatus}</Badge>
										{#if !t.resultIsCurrent}
											<Badge tone="warning" >stale</Badge>
										{/if}
									{:else}
										<Badge tone="neutral">never run</Badge>
									{/if}
								</div>

								{#if t.description}
									<p class="mt-1 text-xs text-surface-600-400">{t.description}</p>
								{/if}
								<div class="mt-1 text-xs text-surface-600-400">
									{t.targetDevices.length} target device(s) · last run {fmt(t.lastRunAt)}
								</div>

								{#if t.lastStatus && !t.resultIsCurrent}
									<p class="mt-1 text-xs text-warning-600-400">
										The workflow changed after this result — it is not evidence for the current
										definition.
									</p>
								{/if}

								{#if t.lastFailures.length > 0}
									<ul class="mt-1 list-inside list-disc text-xs text-error-600-400">
										{#each t.lastFailures as f (f)}<li>{f}</li>{/each}
									</ul>
								{/if}
							</div>

							<RoleGate require="operator">
								<div class="flex gap-1">
									<IconButton
										label={`Run ${t.name}`}
										disabled={runningId === t.id}
										onclick={() => run(t)}
									>
										{#if runningId === t.id}<Spinner size="sm" />{:else}<Play size={14} />{/if}
									</IconButton>
									<IconButton label="Delete test" onclick={() => remove(t)}><Trash2 size={14} /></IconButton>
								</div>
							</RoleGate>
						</div>
					</div>
				{/each}
			</div>
		{/if}
	</div>
{:else}
	<div class="space-y-3">
		<p class="text-sm text-surface-600-400">
			Immutable snapshots written at promotion time — what actually ran, not what the live row says
			now.
		</p>

		{#if versions.length === 0}
			<div class="rounded-xl border border-surface-200-800 px-4 py-10 text-center text-sm text-surface-600-400">
				No promoted versions yet.
			</div>
		{:else}
			<div class="space-y-2">
				{#each versions as v (v.id)}
					<div class="flex items-start gap-3 rounded-xl border border-surface-200-800 p-3">
						<div class="min-w-0 flex-1">
							<div class="flex flex-wrap items-center gap-2">
								<span class="font-medium">v{v.version}</span>
								<Badge>{v.environment}</Badge>
							</div>
							<div class="mt-1 text-xs text-surface-600-400">
								{fmt(v.promotedAt)} by {v.promotedBy || '—'}
							</div>
							{#if v.changeSummary}
								<p class="mt-1 text-xs text-surface-600-400">{v.changeSummary}</p>
							{/if}
							<code class="mt-1 block truncate text-xs text-surface-600-400" title={v.schemaHash}>
								{v.schemaHash}
							</code>
						</div>
						<RoleGate require="operator">
							<IconButton label={`Restore version ${v.version}`} onclick={() => restore(v)}>
								<RotateCcw size={14} />
							</IconButton>
						</RoleGate>
					</div>
				{/each}
			</div>
		{/if}
	</div>
{/if}

<Modal bind:open={modalOpen} title="New acceptance test" size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		<Input label="Name" bind:value={fName} required />
		<Textarea label="Description" bind:value={fDescription} rows={2} />

		{#if showInputs}
			<div>
				<div class="mb-1.5 text-sm font-medium">Input</div>
				<p class="mb-2 text-xs text-surface-600-400">
					The payload this test runs with.
					{#if !workflow.inputSchema}
						Derived from the workflow's <code>{'{{ input.* }}'}</code> references — it declares no
						input schema.
					{/if}
				</p>
				<JsonSchemaForm {schema} bind:value={fInput} />
			</div>
		{/if}

		<div>
			<div class="mb-1.5 text-sm font-medium">Target devices</div>
			<DevicePicker bind:selected={fTargets} environment={workflow.environment} />
		</div>

		<CodeEditor label="Assertions" language="json" bind:value={fAssertions} rows={8} />
		<p class="text-xs text-surface-600-400">
			Kinds: <code>status_equals</code>, <code>step_succeeded</code>, <code>step_failed</code>,
			<code>output_equals</code>, <code>output_contains</code>. For the output kinds,
			<code>path</code> is <code>node.output.field</code>. A test with no assertions is reported as a
			failure, not a pass.
		</p>
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Create</Button>
	{/snippet}
</Modal>
