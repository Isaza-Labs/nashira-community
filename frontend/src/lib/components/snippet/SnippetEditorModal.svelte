<script lang="ts">
	// The snippet editor, extracted from /admin/snippets so the workflow view can
	// open the same form rather than a second one that drifts from it. A workflow
	// author who finds a bad script in the plan should not have to leave the page,
	// find the snippet in a list and remember which one it was.
	//
	// Owns the form and the save; the caller owns when it opens and what happens
	// after, because "reload the list" and "reload the plan" are different jobs.
	import {
		listSnippetTypes,
		listSnippets,
		getSnippet,
		createSnippet,
		updateSnippet,
		TARGET_MODES,
		IDEMPOTENCY_TIERS,
		type Snippet,
		type SnippetPayload
	} from '$lib/api/snippets.api';
	import {
		Modal,
		Button,
		Input,
		Select,
		Textarea,
		CodeEditor,
		Checkbox,
		Mermaid,
		Alert,
		Spinner,
		toast
	} from '$lib/components/ui';

	let {
		open = $bindable(false),
		snippet = null,
		onsaved
	}: {
		open?: boolean;
		/** The snippet to edit; null opens the form empty, to create one. */
		snippet?: Snippet | null;
		onsaved?: (saved: Snippet) => void;
	} = $props();

	let types = $state<string[]>([]);
	let typesLoading = $state(true);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fType = $state('');
	let fDescription = $state('');
	let fCode = $state('');
	let fInputSchema = $state('');
	let fOutputSchema = $state('');
	let fTargetMode = $state('once');
	let fTimeout = $state('60');
	let fIdempotency = $state('');
	let fDiagram = $state('');
	let fNetworkEnabled = $state(false);
	// Carried, not edited. The form has no control for either, but both have to
	// survive a round trip — above all `changes_state`: for a python_snippet, null
	// means "the author has not said" and every step using the row FAILS until a
	// node declares it. Losing it on a save would look like nothing happened.
	let fScriptLanguage = $state<string | null>(null);
	let fChangesState = $state<boolean | null>(null);

	const typeOptions = $derived(types.map((t) => ({ value: t, label: t })));
	const targetOptions = TARGET_MODES.map((t) => ({ value: t, label: t }));
	const idempotencyOptions = IDEMPOTENCY_TIERS.map((t) => ({
		value: t,
		label: t === '' ? "— the handler's default —" : t
	}));

	// `transform` carries its mapping in the code body; python carries its script;
	// ansible carries its playbook YAML.
	const usesCode = $derived(
		fType === 'transform' || fType === 'python_snippet' || fType === 'ansible_playbook'
	);
	const isPython = $derived(fType === 'python_snippet');

	// The API refuses these four without a diagram (SnippetCatalog.ValidateLogicDiagram),
	// so the field says so before the save does. Mirrors the backend list rather than
	// guessing from `usesCode`: `jmespath` carries no code body and still needs one.
	const diagramRequired = $derived(
		fType === 'python_snippet' || fType === 'python' || fType === 'transform' || fType === 'jmespath'
	);

	// Loaded once on mount, not on open: the type picker is what stops the form
	// producing an unrunnable snippet, so it has to be there when the form is.
	$effect(() => {
		listSnippetTypes()
			.then((t) => (types = t))
			.catch((e) => toast.fromError(e, "Couldn't load the snippet types"))
			.finally(() => (typesLoading = false));
	});

	// Plain, not `$state`: this tracks the open transition and must not itself be a
	// dependency of the effect below, or hydrating would retrigger it.
	let hydrated = false;

	function hydrate() {
		formError = '';
		const s = snippet;
		fName = s?.name ?? '';
		fType = s?.type ?? types[0] ?? '';
		fDescription = s?.description ?? '';
		fCode = s?.code ?? '';
		fInputSchema = s?.inputSchema ?? '';
		fOutputSchema = s?.outputSchema ?? '';
		fTargetMode = s?.targetMode ?? 'once';
		fTimeout = String(s?.timeoutSeconds ?? 60);
		fIdempotency = s?.idempotency ?? '';
		fDiagram = s?.logicDiagramMermaid ?? '';
		fNetworkEnabled = s?.networkEnabled ?? false;
		fScriptLanguage = s?.scriptLanguage ?? null;
		fChangesState = s?.changesState ?? null;
		sourceId = '';
	}

	// ── Start from an existing snippet (create only) ────────────────────────
	//
	// The Type picker lists HANDLER TYPES, and the API validates against exactly
	// that list — so a seeded baseline like the paramiko SSH primitive can never
	// appear there: it is a `python_snippet` with a particular body, not a type of
	// its own. People go looking for it in that dropdown, which is why this sits
	// right above it rather than only as a Duplicate button on the list page.
	let sources = $state<Snippet[]>([]);
	let sourceId = $state('');
	let prefilling = $state(false);

	$effect(() => {
		// Only for creating. Editing a snippet from a template is not a thing —
		// that is what the fields below already are.
		if (!open || snippet) return;
		listSnippets()
			.then((r) => (sources = r.items))
			// Non-fatal: the form still creates a snippet from scratch, which is what
			// it did before this picker existed.
			.catch(() => (sources = []));
	});

	// Reacts to the picker rather than to an onchange handler: the shared Select
	// exposes only a bound value. `lastPrefilled` is plain, not `$state`, so
	// writing it here cannot retrigger this effect.
	let lastPrefilled = '';
	$effect(() => {
		const id = sourceId;
		if (id === lastPrefilled) return;
		lastPrefilled = id;
		if (id) prefillFrom(id);
	});

	async function prefillFrom(id: string) {
		if (!id) return;
		prefilling = true;
		try {
			// Re-read: the list is a summary and the body is the point of copying.
			const s = await getSnippet(id);
			fName = `${s.name} (copy)`;
			fType = s.type;
			fDescription = s.description ?? '';
			fCode = s.code ?? '';
			fInputSchema = s.inputSchema ?? '';
			fOutputSchema = s.outputSchema ?? '';
			fTargetMode = s.targetMode;
			fTimeout = String(s.timeoutSeconds);
			fIdempotency = s.idempotency ?? '';
			fDiagram = s.logicDiagramMermaid ?? '';
			fNetworkEnabled = s.networkEnabled;
			fScriptLanguage = s.scriptLanguage;
			fChangesState = s.changesState;
		} catch (e) {
			toast.fromError(e, "Couldn't read that snippet");
			sourceId = '';
		} finally {
			prefilling = false;
		}
	}

	// Fill the form on the way open and not on every change, so a keystroke or a
	// late-arriving type list cannot discard what has been typed.
	$effect(() => {
		if (open && !hydrated) {
			hydrated = true;
			hydrate();
		} else if (!open) {
			hydrated = false;
		}
	});

	function payload(): SnippetPayload {
		return {
			name: fName.trim(),
			type: fType,
			description: fDescription,
			code: fCode,
			inputSchema: fInputSchema,
			outputSchema: fOutputSchema,
			targetMode: fTargetMode,
			timeoutSeconds: Number.parseInt(fTimeout, 10) || 60,
			idempotency: fIdempotency,
			logicDiagramMermaid: fDiagram,
			networkEnabled: fNetworkEnabled,
			scriptLanguage: fScriptLanguage,
			changesState: fChangesState
		};
	}

	async function save() {
		if (!fName.trim()) {
			formError = 'Name is required.';
			return;
		}
		if (!fType) {
			formError = 'Type is required.';
			return;
		}
		formError = '';
		saving = true;
		try {
			const editing = snippet;
			const saved = editing
				? await updateSnippet(editing.id, payload())
				: await createSnippet(payload());
			open = false;
			toast.success(editing ? 'Snippet updated' : 'Snippet created');
			onsaved?.(saved);
		} catch (e) {
			toast.fromError(e, 'Failed to save the snippet');
		} finally {
			saving = false;
		}
	}
</script>

<Modal bind:open title={snippet ? 'Edit snippet' : 'New snippet'} size="lg">
	{#if typesLoading}
		<div class="flex justify-center py-10"><Spinner /></div>
	{:else}
		<div class="space-y-3">
			{#if formError}<Alert tone="error">{formError}</Alert>{/if}

			{#if !snippet && sources.length > 0}
				<Select
					label="Start from"
					bind:value={sourceId}
					disabled={prefilling}
					options={[
						{ value: '', label: '— blank —' },
						...sources.map((s) => ({ value: s.id, label: `${s.name} · ${s.type}` }))
					]}
				/>
				{#if sourceId}
					<p class="text-xs text-surface-600-400">
						Every field below was filled from that snippet. Nothing is saved until you
						press save, so edit whatever you need first.
					</p>
				{/if}
			{/if}

			<div class="grid gap-3 sm:grid-cols-2">
				<Input label="Name" bind:value={fName} required />
				<Select label="Type" bind:value={fType} options={typeOptions} />
			</div>
			<Textarea label="Description" bind:value={fDescription} rows={2} />

			<div class="grid gap-3 sm:grid-cols-3">
				<Select label="Target mode" bind:value={fTargetMode} options={targetOptions} />
				<Input label="Timeout (s)" bind:value={fTimeout} type="number" />
				<Select label="Idempotency" bind:value={fIdempotency} options={idempotencyOptions} />
			</div>

			{#if fTargetMode === 'per_device'}
				<Alert tone="neutral">
					This step runs once per target device, with <code>{'{{ device.* }}'}</code> resolved. A run with
					no target devices will fail the node rather than report an empty success.
				</Alert>
			{/if}
			{#if fIdempotency === 'idempotent'}
				<Alert tone="warning">
					Declaring a step idempotent makes it eligible for automatic retry. Only do this when
					re-running it with the same input is genuinely safe.
				</Alert>
			{/if}

			{#if usesCode}
				<CodeEditor
					label={isPython ? 'Script' : 'Mapping'}
					language={isPython ? 'markdown' : 'json'}
					bind:value={fCode}
					rows={isPython ? 12 : 8}
					hint={isPython
						? 'Python. Reads `input`, sets `result`. Imports are limited to the admin allowlist.'
						: 'JSON object of output name → path over the step input, e.g. {"ip":"device.ip"}'}
				/>
			{/if}

			{#if isPython}
				<Checkbox bind:checked={fNetworkEnabled} label="Allow network modules" />
				{#if fNetworkEnabled}
					<Alert tone="error">
						Enabling this requires an administrator and offers the snippet every
						network-flagged module on the allowlist. It is a per-snippet decision, not a global one.
					</Alert>
				{/if}
				<p class="text-xs text-surface-600-400">
					Imports are limited to the allowlist —
					<a class="underline hover:text-primary-700-300" href="/admin/python-modules">
						manage Python modules
					</a>.
				</p>
			{/if}

			<!-- The thing this step talks to lives on another screen; say which, and go
			     there. Otherwise the author has to remember what "integration_action"
			     was pointed at and find it by hand. -->
			{#if fType === 'integration_action'}
				<p class="text-xs text-surface-600-400">
					The base URL and credentials come from the integration named in the step's
					<code class="font-mono">config_overrides</code> —
					<a class="underline hover:text-primary-700-300" href="/admin/integrations">
						browse integrations and their action catalogue
					</a>.
				</p>
			{:else if fType === 'mcp_call'}
				<p class="text-xs text-surface-600-400">
					Tool names come from a synced catalogue —
					<a class="underline hover:text-primary-700-300" href="/admin/mcp">
						browse MCP servers and their tools
					</a>.
				</p>
			{:else if fType === 'ssh'}
				<p class="text-xs text-surface-600-400">
					To avoid branching on platform inside the step, resolve the CLI through
					<a class="underline hover:text-primary-700-300" href="/admin/vendor-commands">
						vendor commands
					</a>.
				</p>
			{:else if fType === 'git'}
				<p class="text-xs text-surface-600-400">
					The step names an <code class="font-mono">operation</code> and a
					<code class="font-mono">repository_id</code> in its
					<code class="font-mono">config_overrides</code> —
					<a class="underline hover:text-primary-700-300" href="/git">
						browse registered repositories
					</a>
					for the id.
				</p>
			{/if}

			<div class="grid gap-3 sm:grid-cols-2">
				<CodeEditor label="Input schema" language="json" bind:value={fInputSchema} rows={6} hint="Optional, advisory" />
				<CodeEditor label="Output schema" language="json" bind:value={fOutputSchema} rows={6} hint="Optional, advisory" />
			</div>

			<!-- Source and drawing side by side. A diagram nobody can see while writing
			     it is a diagram nobody checks: this field held Mermaid the app never
			     drew anywhere, so a typo in an arrow survived until someone pasted the
			     source into another tool. The preview redraws as it is typed and says
			     what Mermaid rejected. -->
			<div class="grid gap-3 lg:grid-cols-2">
				<Textarea
					label="Logic diagram (Mermaid)"
					bind:value={fDiagram}
					rows={10}
					spellcheck="false"
					placeholder={'flowchart TD\n    in([Input: devices]) --> step[Do the thing]\n    step --> out([Output: rows])'}
					hint={diagramRequired
						? `Required for ${fType} — what the step does, so the next reader does not have to read the code.`
						: 'Optional. If you supply one it has to be a real diagram.'}
				/>
				<div class="min-w-0">
					<div class="mb-1 text-xs font-medium text-surface-700-300">Preview</div>
					<div
						class="min-h-[8rem] overflow-x-auto rounded-md border border-surface-200-800 bg-surface-50-900 p-2"
					>
						<Mermaid code={fDiagram} placeholder="The diagram appears here as you type." />
					</div>
				</div>
			</div>
		</div>
	{/if}

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (open = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} disabled={typesLoading} onclick={save}>Save</Button>
	{/snippet}
</Modal>
