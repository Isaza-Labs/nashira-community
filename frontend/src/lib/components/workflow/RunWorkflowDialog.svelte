<script lang="ts">
	// What a manual run is asked for before it executes: the runtime inputs and the
	// devices to fan out over. The API has always accepted both; the UI used to send
	// neither, so a workflow whose nodes reference `{{ input.host }}` ran with that
	// unresolved and a per-device workflow ran against nothing.
	//
	// The rollback warning stays here rather than in a separate confirm step. Splitting
	// them meant the user agreed to the risk before seeing what the run would target.
	import { Alert, Button, Modal, Spinner } from '$lib/components/ui';
	import JsonSchemaForm from '$lib/components/JsonSchemaForm.svelte';
	import DevicePicker from '$lib/components/device/DevicePicker.svelte';
	import { listSnippets } from '$lib/api/snippets.api';
	import { effectiveInputSchema, hasInputFields } from '$lib/workflow/deriveInputs';
	import { buildSchemaDefaults } from '$lib/workflow/schemaDefaults';
	import type { Workflow, WorkflowPlan, RunWorkflowPayload } from '$lib/api/workflows.api';
	import { Play } from 'lucide-svelte';

	let {
		open = $bindable(false),
		workflow,
		plan,
		running = false,
		onrun
	}: {
		open?: boolean;
		workflow: Workflow;
		plan: WorkflowPlan;
		running?: boolean;
		onrun: (payload: RunWorkflowPayload) => void;
	} = $props();

	// The declared schema when the author wrote one, otherwise one derived from the
	// `{{ input.X }}` references in the DAG — which is the usual case, since most
	// workflows are authored by the agent and nothing makes it declare a schema.
	const schema = $derived(effectiveInputSchema(workflow));
	const showInputs = $derived(hasInputFields(schema));

	let input = $state<Record<string, unknown>>({});
	let targets = $state<string[]>([]);

	// Reseed on each open. Carrying the previous run's values over would silently
	// re-target a second run at the first one's devices.
	let wasOpen = false;
	// The dialog lives on the Plan tab and a <dialog>'s children mount whether or not
	// it is shown, so nothing inside may fetch on mount — that would put a snippet and
	// a device query on every visit to a workflow page for a run nobody asked for.
	let everOpened = $state(false);

	$effect(() => {
		if (open && !wasOpen) {
			input = buildSchemaDefaults(schema);
			targets = [];
			everOpened = true;
		}
		wasOpen = open;
	});

	// Whether any node in this workflow actually fans out per device. When every node
	// runs `once`, the picker is optional and says so — otherwise the user is nudged
	// into selecting devices the run will never use.
	let targetModes = $state<Map<string, string>>(new Map());
	let modesLoaded = $state(false);

	$effect(() => {
		if (!everOpened || modesLoaded) return;
		listSnippets(200, 0)
			.then((r) => (targetModes = new Map(r.items.map((s) => [s.id, s.targetMode]))))
			.catch(() => (targetModes = new Map()))
			.finally(() => (modesLoaded = true));
	});

	const requiresDevices = $derived(
		workflow.nodes.some((n) => n.snippetId && targetModes.get(n.snippetId) === 'per_device')
	);

	function submit() {
		onrun({
			input: showInputs ? input : undefined,
			targetDevices: targets
		});
	}

	const runLabel = $derived(
		targets.length > 0
			? `Run on ${targets.length} ${targets.length === 1 ? 'device' : 'devices'}`
			: requiresDevices
				? 'Run (no devices selected)'
				: 'Run without devices'
	);
</script>

<Modal bind:open title={`Run "${workflow.name}"`} size="lg">
	<div class="space-y-4">
		{#if plan.rollbackReversible}
			<Alert tone="primary">
				This executes every step against live systems now. The workflow is fully reversible.
			</Alert>
		{:else}
			<Alert tone="warning" title="Contains non-reversible steps">
				{plan.nonReversibleNodes.length}
				{plan.nonReversibleNodes.length === 1 ? 'step' : 'steps'} cannot be undone automatically. If
				a later step fails, they stay applied.
			</Alert>
		{/if}

		{#if showInputs}
			<section class="space-y-2">
				<div>
					<h3 class="text-sm font-semibold">Runtime inputs</h3>
					<p class="text-xs text-surface-600-400">
						{#if workflow.inputSchema}
							Declared by this workflow. Feeds <code>{'{{ input.* }}'}</code> in every node.
						{:else}
							Derived from the <code>{'{{ input.* }}'}</code> references in this workflow's nodes —
							it declares no input schema, so these are inferred as text.
						{/if}
					</p>
				</div>
				<JsonSchemaForm {schema} bind:value={input} readonly={running} />
			</section>
		{/if}

		<section class="space-y-2">
			<div>
				<h3 class="text-sm font-semibold">
					Target devices{requiresDevices ? '' : ' (optional)'}
				</h3>
				{#if !modesLoaded}
					<p class="text-xs text-surface-600-400">Checking which steps run per device…</p>
				{:else if !requiresDevices}
					<p class="text-xs text-surface-600-400">
						No step in this workflow runs per device — leave this empty to execute once with no
						device context.
					</p>
				{/if}
			</div>
			{#if everOpened}
				<DevicePicker
					bind:selected={targets}
					environment={workflow.environment}
					disabled={running}
				/>
			{/if}
		</section>

		{#if running}
			<div class="flex items-center gap-2 text-sm text-surface-600-400">
				<Spinner size="sm" />
				Running — every step executes synchronously, so this can take a while.
			</div>
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" disabled={running} onclick={() => (open = false)}>Cancel</Button>
		<Button
			variant={plan.rollbackReversible ? 'primary' : 'danger'}
			loading={running}
			disabled={running || plan.order.length === 0}
			onclick={submit}
		>
			<Play size={15} />{runLabel}
		</Button>
	{/snippet}
</Modal>
