<script lang="ts">
	import {
		listTriggers,
		createTrigger,
		updateTrigger,
		deleteTrigger,
		rotateSecret,
		type WorkflowTrigger,
		type TriggerPayload,
		type TriggerType
	} from '$lib/api/triggers.api';
	import {
		Button,
		Modal,
		Badge,
		IconButton,
		Input,
		Textarea,
		Select,
		Checkbox,
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
	import { Plus, Pencil, Trash2, KeyRound } from 'lucide-svelte';

	// Cron schedules and inbound webhooks for one workflow. The workflow itself is
	// needed, not just its id: an unattended run resolves the same inputs and the same
	// targets a manual one does, so it is configured with the same form.
	let { workflowId, workflow }: { workflowId: string; workflow: Workflow } = $props();

	// The declared input schema, or one derived from the DAG's `{{ input.X }}` refs.
	const schema = $derived(effectiveInputSchema(workflow));
	const showInputs = $derived(hasInputFields(schema));

	let items = $state<WorkflowTrigger[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<WorkflowTrigger | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fType = $state<TriggerType>('cron');
	let fDescription = $state('');
	let fCron = $state('0 2 * * *');
	let fTimezone = $state('UTC');
	let fTargets = $state<string[]>([]);
	let fDefaults = $state<Record<string, unknown>>({});
	let fAllowUnsigned = $state(false);
	let fAllowOverride = $state(false);
	let fEnabled = $state(true);

	// The secret is returned exactly once. If this dialog is dismissed without
	// copying it, the only recovery is a rotation.
	let secretOpen = $state(false);
	let secretValue = $state('');
	let secretRoute = $state('');

	const typeOptions = [
		{ value: 'cron', label: 'cron — on a schedule' },
		{ value: 'webhook', label: 'webhook — on an inbound call' }
	];

	function statusTone(s: string | null): Tone {
		if (s === 'completed') return 'success';
		if (s === 'failed' || s === 'error') return 'error';
		return 'neutral';
	}

	function fmt(iso: string | null): string {
		return iso ? new Date(iso).toLocaleString() : '—';
	}

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listTriggers(workflowId)).items;
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
		fType = 'cron';
		fDescription = '';
		fCron = '0 2 * * *';
		fTimezone = 'UTC';
		fTargets = [];
		fDefaults = buildSchemaDefaults(schema);
		fAllowUnsigned = false;
		fAllowOverride = false;
		fEnabled = true;
		modalOpen = true;
	}

	function openEdit(t: WorkflowTrigger) {
		editing = t;
		formError = '';
		fName = t.name;
		fType = t.type;
		fDescription = t.description ?? '';
		fCron = t.cronExpression ?? '';
		fTimezone = t.timezone;
		fTargets = [...t.targetDevices];
		// Seed from the schema so a key added to the workflow after this trigger was
		// saved still shows up, then overlay what the trigger actually stored.
		fDefaults = {
			...buildSchemaDefaults(schema),
			...(t.inputDefaults && typeof t.inputDefaults === 'object' && !Array.isArray(t.inputDefaults)
				? (t.inputDefaults as Record<string, unknown>)
				: {})
		};
		fAllowUnsigned = t.allowUnsigned;
		fAllowOverride = t.allowTargetOverride;
		fEnabled = t.enabled;
		modalOpen = true;
	}

	function payload(): TriggerPayload {
		return {
			name: fName.trim(),
			type: fType,
			description: fDescription,
			cronExpression: fCron,
			timezone: fTimezone,
			targetDevices: fTargets,
			inputDefaults: fDefaults,
			allowUnsigned: fAllowUnsigned,
			allowTargetOverride: fAllowOverride,
			enabled: fEnabled
		};
	}

	async function save() {
		if (!fName.trim()) {
			formError = 'Name is required.';
			return;
		}
		if (fType === 'cron' && !fCron.trim()) {
			formError = 'A cron expression is required.';
			return;
		}
		formError = '';
		saving = true;
		try {
			const saved = editing
				? await updateTrigger(workflowId, editing.id, payload())
				: await createTrigger(workflowId, payload());
			modalOpen = false;
			toast.success(editing ? 'Trigger updated' : 'Trigger created');

			if (saved.secret) {
				secretValue = saved.secret;
				secretRoute = saved.route ?? '';
				secretOpen = true;
			}
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the trigger');
		} finally {
			saving = false;
		}
	}

	async function remove(t: WorkflowTrigger) {
		const ok = await confirm({
			title: 'Delete trigger?',
			message:
				t.type === 'webhook'
					? `"${t.name}" will stop accepting deliveries immediately. Anything calling its URL will start getting 401.`
					: `"${t.name}" will no longer fire on its schedule.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteTrigger(workflowId, t.id);
			items = items.filter((x) => x.id !== t.id);
			toast.success('Trigger deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the trigger");
		}
	}

	async function rotate(t: WorkflowTrigger) {
		const ok = await confirm({
			title: 'Rotate secret?',
			message: `The current secret for "${t.name}" stops working immediately. Any caller still using it will get 401 until you update it.`,
			tone: 'danger',
			confirmLabel: 'Rotate'
		});
		if (!ok) return;
		try {
			const updated = await rotateSecret(workflowId, t.id);
			secretValue = updated.secret ?? '';
			secretRoute = updated.route ?? '';
			secretOpen = true;
			await load();
		} catch (e) {
			toast.fromError(e, "Couldn't rotate the secret");
		}
	}

	const hookUrl = $derived(
		secretRoute ? `${typeof location !== 'undefined' ? location.origin : ''}/api/hooks/${secretRoute}` : ''
	);
</script>

<div class="space-y-3">
	<div class="flex items-center justify-between">
		<p class="text-sm text-surface-600-400">
			Cron schedules and inbound webhooks that run this workflow unattended.
		</p>
		<RoleGate require="operator">
			<Button variant="secondary" size="sm" onclick={openCreate}><Plus size={14} />New trigger</Button>
		</RoleGate>
	</div>

	{#if loading}
		<div class="flex justify-center py-10"><Spinner /></div>
	{:else if error}
		<ErrorState {error} onRetry={load} compact />
	{:else if items.length === 0}
		<div class="rounded-xl border border-surface-200-800 px-4 py-10 text-center text-sm text-surface-600-400">
			No triggers. This workflow only runs when someone starts it.
		</div>
	{:else}
		<div class="space-y-2">
			{#each items as t (t.id)}
				<div class="rounded-xl border border-surface-200-800 p-3">
					<div class="flex items-start gap-3">
						<div class="min-w-0 flex-1">
							<div class="flex flex-wrap items-center gap-2">
								<span class="font-medium">{t.name}</span>
								<Badge>{t.type}</Badge>
								{#if !t.enabled}<Badge tone="warning">disabled</Badge>{/if}
								{#if t.type === 'webhook' && !t.hasSecret && t.allowUnsigned}
									<Badge tone="error">unauthenticated</Badge>
								{/if}
							</div>

							{#if t.type === 'cron'}
								<div class="mt-1 text-xs text-surface-600-400">
									<code>{t.cronExpression}</code> · {t.timezone} · next {fmt(t.nextRunAt)}
								</div>
							{:else}
								<div class="mt-1 text-xs text-surface-600-400">
									<code>/api/hooks/{t.route}</code>
									{#if t.allowTargetOverride} · body may narrow targets{/if}
								</div>
							{/if}

							<div class="mt-1 flex flex-wrap items-center gap-2 text-xs text-surface-600-400">
								<span>fired {t.fireCount}×</span>
								{#if t.lastRunStatus}
									<Badge tone={statusTone(t.lastRunStatus)}>{t.lastRunStatus}</Badge>
									<span>{fmt(t.lastRunAt)}</span>
								{/if}
							</div>

							{#if t.lastError}
								<p class="mt-1 text-xs text-error-600-400">{t.lastError}</p>
							{/if}
						</div>

						<RoleGate require="operator">
							<div class="flex gap-1">
								{#if t.type === 'webhook'}
									<IconButton label={`Rotate ${t.name} secret`} onclick={() => rotate(t)}>
										<KeyRound size={14} />
									</IconButton>
								{/if}
								<IconButton label="Edit trigger" onclick={() => openEdit(t)}><Pencil size={14} /></IconButton>
								<IconButton label="Delete trigger" onclick={() => remove(t)}><Trash2 size={14} /></IconButton>
							</div>
						</RoleGate>
					</div>
				</div>
			{/each}
		</div>
	{/if}
</div>

<Modal bind:open={modalOpen} title={editing ? 'Edit trigger' : 'New trigger'} size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		<Input label="Name" bind:value={fName} required />
		{#if !editing}
			<Select label="Type" bind:value={fType} options={typeOptions} />
		{:else}
			<p class="text-xs text-surface-600-400">
				The type cannot be changed — a cron trigger has no route or secret and a webhook has no
				schedule, so switching would leave the trigger half-configured.
			</p>
		{/if}
		<Textarea label="Description" bind:value={fDescription} rows={2} />

		{#if fType === 'cron'}
			<div class="grid gap-3 sm:grid-cols-2">
				<Input label="Cron expression" bind:value={fCron} required hint="5 fields, or 6 with seconds" />
				<Input label="Timezone" bind:value={fTimezone} hint="IANA id, e.g. Europe/Madrid" />
			</div>
			<p class="text-xs text-surface-600-400">
				Evaluated in that timezone, not UTC — a 02:00 window stays at 02:00 local across DST.
			</p>
		{:else}
			<Checkbox bind:checked={fAllowOverride} label="Let the delivery body choose target devices" />
			{#if fAllowOverride}
				<Alert tone="warning">
					A webhook caller authenticates with a shared secret, not a user session, so the run skips
					the role check a manual run passes. Body targets can only <strong>narrow</strong> the list
					below — but if that list is empty, a leaked secret can target any device.
				</Alert>
			{/if}
			<Checkbox bind:checked={fAllowUnsigned} label="Accept unsigned deliveries (testing only)" />
			{#if fAllowUnsigned}
				<Alert tone="error">
					With no secret set, anyone who learns the URL can run this workflow.
				</Alert>
			{/if}
		{/if}

		<div>
			<div class="mb-1.5 text-sm font-medium">Target devices</div>
			<DevicePicker bind:selected={fTargets} environment={workflow.environment} />
		</div>

		{#if showInputs}
			<div>
				<div class="mb-1.5 text-sm font-medium">Input defaults</div>
				<p class="mb-2 text-xs text-surface-600-400">
					Baked into every fire of this trigger and merged under the run's input.
					{#if fType === 'webhook'}
						A delivery body may override a key but not remove one.
					{/if}
					{#if !workflow.inputSchema}
						Derived from this workflow's <code>{'{{ input.* }}'}</code> references — it declares no
						input schema.
					{/if}
				</p>
				<JsonSchemaForm {schema} bind:value={fDefaults} />
			</div>
		{/if}

		<Checkbox bind:checked={fEnabled} label="Enabled" />
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>

<Modal bind:open={secretOpen} title="Webhook secret" size="lg">
	<div class="space-y-3">
		<Alert tone="warning">
			This is the only time the secret is shown. There is no endpoint that returns it again — if you
			lose it, you have to rotate.
		</Alert>

		{#if hookUrl}
			<div>
				<div class="mb-1 text-sm font-medium">URL</div>
				<code class="block overflow-x-auto rounded-lg border border-surface-200-800 bg-surface-100-900 p-2 text-xs"
					>{hookUrl}</code
				>
			</div>
		{/if}

		<div>
			<div class="mb-1 text-sm font-medium">Secret</div>
			<code class="block overflow-x-auto rounded-lg border border-surface-200-800 bg-surface-100-900 p-2 text-xs"
				>{secretValue}</code
			>
		</div>

		<p class="text-xs text-surface-600-400">
			Sign the raw request body with HMAC-SHA256 and send it as
			<code>X-Nashira-Signature</code> (hex, optionally <code>sha256=</code>-prefixed). Callers that
			cannot sign may send the secret verbatim as <code>X-Nashira-Token</code>.
		</p>
	</div>
	{#snippet footer()}
		<Button variant="primary" onclick={() => (secretOpen = false)}>I've copied it</Button>
	{/snippet}
</Modal>
