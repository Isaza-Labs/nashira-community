<script lang="ts">
	// The visual half of the policy editor: a guided form over the rule document.
	//
	// The parent owns the raw JSON string; this component parses it, renders typed
	// controls over it, and re-serialises whenever one of them changes. Keeping the
	// string as the single source of truth is what lets the Visual and JSON tabs sit
	// next to each other without drifting — whatever the JSON says is what shows
	// here, and whatever is picked here is what the JSON says.
	//
	// Two shapes, one per evaluator:
	//   deny — checked before a run. Matchers under `when`, AND-joined.
	//   gate — checked before a promotion. `on`/`from`/`to` plus a `require` list.
	//
	// Only fields this build can actually evaluate are offered. A builder that emits
	// a matcher nothing reads produces a policy that looks like a guardrail and
	// guards nothing, which is the one failure mode a guardrail must not have.
	import { untrack } from 'svelte';
	import { Input, Select, IconButton, Button, CodeEditor } from '$lib/components/ui';
	import { X, Plus } from 'lucide-svelte';
	import { listDevices } from '$lib/api/devices.api';
	import { listPools } from '$lib/api/pools.api';
	import { listSnippetTypes } from '$lib/api/snippets.api';
	import OptionChips from './OptionChips.svelte';

	let {
		ruleJson = $bindable(''),
		onNotice
	}: {
		ruleJson?: string;
		// Reports what the visual editor could not represent — a parse failure, or a
		// clause this build cannot evaluate. A callback rather than a bound prop
		// because the flow is strictly one-way, and binding a record property to a
		// key that does not exist yet is rejected by Svelte.
		onNotice?: (message: string | null) => void;
	} = $props();

	type Mode = 'deny' | 'gate';
	type Scope = 'this_workflow' | 'any_workflow';
	type RequirementType = 'successful_runs' | 'last_successful_run_within';

	// One flat requirement shape instead of a discriminated union: every field is a
	// concrete string, so a control never binds to `undefined` (Svelte rejects that),
	// and switching type keeps whatever was already typed in the other fields.
	// serialize() emits only the fields the chosen type uses.
	type Requirement = {
		type: RequirementType;
		min: string;
		withinDays: string;
		days: string;
		scope: Scope;
	};

	type DenyWhen = {
		environment: string[];
		device_role: string[];
		device_pool: string[];
		snippet_type: string[];
		description_contains: string[];
	};

	const ENVIRONMENTS = ['draft', 'qa', 'production'];

	const WHEN_KEYS: (keyof DenyWhen)[] = [
		'environment',
		'device_role',
		'device_pool',
		'snippet_type',
		'description_contains'
	];

	const REQUIREMENT_TYPES: RequirementType[] = ['successful_runs', 'last_successful_run_within'];

	const REQUIREMENT_OPTIONS = [
		{ value: 'successful_runs', label: 'N successful runs' },
		{ value: 'last_successful_run_within', label: 'A successful run in the last X days' }
	];

	const SCOPE_OPTIONS = [
		{ value: 'this_workflow', label: 'This workflow' },
		{ value: 'any_workflow', label: 'Any workflow in the source environment' }
	];

	const TRANSITION_OPTIONS = [
		{ value: '', label: '— any —' },
		...ENVIRONMENTS.map((e) => ({ value: e, label: e }))
	];

	const SHAPES = [
		{ id: 'deny', label: 'Deny a run' },
		{ id: 'gate', label: 'Gate a promotion' }
	];

	let mode = $state<Mode>('deny');
	let reason = $state('');
	let when = $state<DenyWhen>(emptyWhen());
	let gateOn = $state('promote');
	let gateFrom = $state('qa');
	let gateTo = $state('production');
	let requirements = $state<Requirement[]>([newRequirement('last_successful_run_within')]);
	let phraseDraft = $state('');

	// Real values for the pickers. Loaded once; if a call fails the picker shows its
	// empty hint rather than falling back to free typing.
	let roleOptions = $state<{ value: string; label: string }[]>([]);
	let poolOptions = $state<{ value: string; label: string }[]>([]);
	let typeOptions = $state<{ value: string; label: string }[]>([]);

	$effect(() => {
		listDevices(500, 0)
			.then((r) => {
				const roles = [...new Set(r.items.map((d) => d.role.trim()).filter(Boolean))].sort();
				roleOptions = roles.map((v) => ({ value: v, label: v }));
			})
			.catch(() => {});
		listPools(200, 0)
			.then((r) => {
				poolOptions = r.items
					.map((p) => ({ value: p.name, label: p.name }))
					.sort((a, b) => a.label.localeCompare(b.label));
			})
			.catch(() => {});
		listSnippetTypes()
			.then((types) => (typeOptions = types.map((t) => ({ value: t, label: t }))))
			.catch(() => {});
	});

	function emptyWhen(): DenyWhen {
		return {
			environment: [],
			device_role: [],
			device_pool: [],
			snippet_type: [],
			description_contains: []
		};
	}

	function newRequirement(type: RequirementType): Requirement {
		return { type, min: '3', withinDays: '0', days: '7', scope: 'this_workflow' };
	}

	// ── JSON ⇄ form ─────────────────────────────────────────────────────────────

	function serialize(): string {
		if (mode === 'gate') {
			const gate: Record<string, unknown> = { action: 'gate', reason, on: gateOn };
			if (gateFrom) gate.from = gateFrom;
			if (gateTo) gate.to = gateTo;
			gate.require = requirements.map((r) => {
				if (r.type === 'successful_runs') {
					const req: Record<string, unknown> = { type: r.type, min: int(r.min, 1) };
					// 0 is the "at any time" sentinel; dropping it keeps the stored rule to
					// the fields that carry meaning.
					if (int(r.withinDays, 0) > 0) req.within_days = int(r.withinDays, 0);
					req.scope = r.scope;
					return req;
				}
				return { type: r.type, days: int(r.days, 1), scope: r.scope };
			});
			return JSON.stringify(gate, null, 2);
		}

		const matchers: Record<string, string[]> = {};
		for (const key of WHEN_KEYS) if (when[key].length > 0) matchers[key] = when[key];
		const deny: Record<string, unknown> = { action: 'deny', reason };
		// An absent `when` denies everything, which is a legitimate thing to express
		// ("nothing runs during the freeze") and is what the evaluator does with it.
		if (Object.keys(matchers).length > 0) deny.when = matchers;
		return JSON.stringify(deny, null, 2);
	}

	// Returns false when the string could not be read, leaving the form as it was so
	// a half-typed rule on the JSON tab does not wipe the visual one.
	function hydrate(json: string): boolean {
		let parsed: Record<string, unknown>;
		try {
			const raw = JSON.parse(json.trim() || '{}');
			if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
				throw new Error('the rule must be a JSON object');
			}
			parsed = raw as Record<string, unknown>;
		} catch (e) {
			notify(`This rule is not valid JSON — ${(e as Error).message}. Fix it on the JSON tab.`);
			return false;
		}

		const dropped: string[] = [];
		const action = str(parsed.action).trim().toLowerCase() || 'deny';
		if (action !== 'deny' && action !== 'gate') dropped.push(`the action "${action}"`);

		reason = str(parsed.reason);

		if (action === 'gate') {
			mode = 'gate';
			gateOn = str(parsed.on) || 'promote';
			gateFrom = str(parsed.from);
			gateTo = str(parsed.to);

			const raw = Array.isArray(parsed.require) ? parsed.require : [];
			const next: Requirement[] = [];
			for (const item of raw) {
				if (!item || typeof item !== 'object' || Array.isArray(item)) {
					dropped.push('a malformed requirement');
					continue;
				}
				const req = item as Record<string, unknown>;
				const type = str(req.type).trim().toLowerCase();
				if (!REQUIREMENT_TYPES.includes(type as RequirementType)) {
					dropped.push(`the requirement "${type || '(unnamed)'}"`);
					continue;
				}
				next.push({
					type: type as RequirementType,
					min: numText(req.min, '3'),
					withinDays: numText(req.within_days, '0'),
					days: numText(req.days, '7'),
					scope: req.scope === 'any_workflow' ? 'any_workflow' : 'this_workflow'
				});
			}
			requirements = next.length > 0 ? next : [newRequirement('last_successful_run_within')];
		} else {
			mode = 'deny';
			const raw =
				parsed.when && typeof parsed.when === 'object' && !Array.isArray(parsed.when)
					? (parsed.when as Record<string, unknown>)
					: {};
			for (const key of Object.keys(raw)) {
				if (!WHEN_KEYS.includes(key as keyof DenyWhen)) dropped.push(`the matcher "${key}"`);
			}
			when = {
				environment: strings(raw.environment),
				device_role: strings(raw.device_role),
				device_pool: strings(raw.device_pool),
				snippet_type: strings(raw.snippet_type),
				description_contains: strings(raw.description_contains)
			};
		}

		// Said out loud rather than swallowed: the visual editor rewrites the whole
		// document on the next click, so anything it cannot represent is about to be
		// lost, and the operator has to know that before it happens.
		const one = dropped.length === 1;
		notify(
			dropped.length > 0
				? `This rule uses ${humanList(dropped)}, which this build cannot evaluate. Editing here drops ${one ? 'it' : 'them'} — stay on the JSON tab to keep ${one ? 'it' : 'them'}.`
				: null
		);
		return true;
	}

	// `echoed` is the last string this component pushed up; `settled` is the
	// canonical form of what is currently on screen. Together they stop the two
	// effects below chasing each other: a value we wrote is not re-parsed, and a
	// value we parsed is not immediately rewritten in our own formatting — so
	// hand-written JSON keeps its shape until a control here is actually touched.
	let echoed = '';
	let settled = '';

	$effect(() => {
		const incoming = ruleJson;
		untrack(() => {
			if (incoming === echoed) return;
			if (hydrate(incoming)) settled = serialize();
		});
	});

	$effect(() => {
		const next = serialize();
		untrack(() => {
			if (next === settled || next === ruleJson) return;
			settled = next;
			echoed = next;
			ruleJson = next;
		});
	});

	// Reading `onNotice` inside an effect would make it a dependency, and the parent
	// hands down a fresh arrow on every render — which is a loop. Called from inside
	// untrack, with the last message remembered so a repeat costs nothing.
	let lastNotice: string | null | undefined;
	function notify(message: string | null) {
		if (lastNotice === message) return;
		lastNotice = message;
		onNotice?.(message);
	}

	// ── Small readers ───────────────────────────────────────────────────────────

	function str(v: unknown): string {
		return typeof v === 'string' ? v : '';
	}

	function strings(v: unknown): string[] {
		// A bare string where an array belongs is what everybody writes first, and the
		// evaluator accepts it, so the builder has to as well.
		if (typeof v === 'string') return v.trim() ? [v] : [];
		if (!Array.isArray(v)) return [];
		return v.filter((x): x is string => typeof x === 'string' && x.trim().length > 0);
	}

	function numText(v: unknown, fallback: string): string {
		return typeof v === 'number' && Number.isFinite(v) ? String(v) : fallback;
	}

	function int(text: string, fallback: number): number {
		const n = Number.parseInt(text, 10);
		return Number.isFinite(n) && n >= 0 ? n : fallback;
	}

	function humanList(items: string[]): string {
		if (items.length === 1) return items[0];
		return `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;
	}

	// ── Edits ───────────────────────────────────────────────────────────────────

	function toggleEnvironment(env: string) {
		when.environment = when.environment.includes(env)
			? when.environment.filter((e) => e !== env)
			: [...when.environment, env];
	}

	function addPhrase() {
		const phrase = phraseDraft.trim();
		phraseDraft = '';
		if (!phrase || when.description_contains.includes(phrase)) return;
		when.description_contains = [...when.description_contains, phrase];
	}

	function removePhrase(phrase: string) {
		when.description_contains = when.description_contains.filter((p) => p !== phrase);
	}

	function addRequirement() {
		requirements = [...requirements, newRequirement('successful_runs')];
	}

	function removeRequirement(index: number) {
		requirements = requirements.filter((_, i) => i !== index);
	}

	const matcherCount = $derived(WHEN_KEYS.filter((k) => when[k].length > 0).length);
</script>

<div class="space-y-4">
	<div class="flex flex-wrap items-center gap-3">
		<span class="text-sm font-medium">Shape</span>
		<div
			class="inline-flex rounded-md border border-surface-300-700 bg-surface-100-900 p-0.5"
			role="radiogroup"
			aria-label="Rule shape"
		>
			{#each SHAPES as shape (shape.id)}
				{@const active = mode === shape.id}
				<button
					type="button"
					role="radio"
					aria-checked={active}
					onclick={() => (mode = shape.id as Mode)}
					class="rounded px-3 py-1 text-xs font-medium transition {active
						? 'bg-primary-500 text-white dark:bg-primary-400 dark:text-primary-950'
						: 'text-surface-600-400 hover:text-surface-950-50'}"
				>
					{shape.label}
				</button>
			{/each}
		</div>
		<span class="text-xs text-surface-600-400">
			{#if mode === 'deny'}
				Blocks a run before it executes.
			{:else}
				Blocks a promotion until the workflow has earned it.
			{/if}
		</span>
	</div>

	<Input
		label="Reason"
		bind:value={reason}
		placeholder={mode === 'deny'
			? 'core routers need a change window'
			: 'production needs a week of clean qa runs'}
		hint="Quoted back to the operator on a refusal — write the sentence you want them to read."
	/>

	{#if mode === 'deny'}
		<div class="space-y-1">
			<span class="text-sm font-medium">Environments</span>
			<div class="flex flex-wrap gap-1.5">
				{#each ENVIRONMENTS as env (env)}
					{@const on = when.environment.includes(env)}
					<button
						type="button"
						aria-pressed={on}
						onclick={() => toggleEnvironment(env)}
						class="rounded-full border px-2.5 py-1 text-xs transition {on
							? 'border-primary-500 bg-primary-500 text-white dark:border-primary-400 dark:bg-primary-400 dark:text-primary-950'
							: 'border-surface-300-700 bg-surface-100-900 text-surface-700-300 hover:bg-surface-200-800'}"
					>
						{env}
					</button>
				{/each}
			</div>
			<p class="text-xs text-surface-600-400">None selected means any environment.</p>
		</div>

		<div class="grid gap-3 sm:grid-cols-2">
			<OptionChips
				label="Device roles"
				options={roleOptions}
				bind:values={when.device_role}
				emptyHint="No roles in inventory"
			/>
			<OptionChips
				label="Device pools"
				options={poolOptions}
				bind:values={when.device_pool}
				emptyHint="No pools defined"
			/>
		</div>

		<OptionChips
			label="Snippet types"
			options={typeOptions}
			bind:values={when.snippet_type}
			emptyHint="No snippet types"
			hint="Read from the workflow's graph — a rule about ssh fires on any workflow containing an SSH step."
		/>

		<div class="space-y-1">
			<span class="text-sm font-medium">Description contains</span>
			{#if when.description_contains.length > 0}
				<div class="flex flex-wrap items-center gap-1.5">
					{#each when.description_contains as phrase (phrase)}
						<span
							class="inline-flex items-center gap-1 rounded-full border border-surface-300-700 bg-surface-100-900 py-0.5 pl-2.5 pr-1 text-xs"
						>
							{phrase}
							<button
								type="button"
								onclick={() => removePhrase(phrase)}
								aria-label={`Remove ${phrase}`}
								class="inline-flex h-4 w-4 items-center justify-center rounded-full transition hover:bg-surface-300-700"
							>
								<X size={10} />
							</button>
						</span>
					{/each}
				</div>
			{/if}
			<div class="flex items-start gap-2">
				<div class="flex-1">
					<Input
						bind:value={phraseDraft}
						placeholder="bgp, reload, shutdown…"
						onkeydown={(e: KeyboardEvent) => {
							if (e.key === 'Enter') {
								e.preventDefault();
								addPhrase();
							}
						}}
					/>
				</div>
				<Button variant="secondary" onclick={addPhrase}><Plus size={15} />Add</Button>
			</div>
			<p class="text-xs text-surface-600-400">
				Case-insensitive substrings of the workflow description; any one of them matching is enough.
			</p>
		</div>

		<p class="text-xs text-surface-600-400">
			{#if matcherCount === 0}
				<strong>No matchers.</strong> This rule denies every run, in every environment.
			{:else}
				{matcherCount}
				{matcherCount === 1 ? 'matcher' : 'matchers'}, all of which must hold — adding one always
				narrows the rule, never widens it.
			{/if}
		</p>
	{:else}
		<div class="grid gap-3 sm:grid-cols-3">
			<Select
				label="On"
				bind:value={gateOn}
				options={[{ value: 'promote', label: 'promote' }]}
				hint="The only transition gated today."
			/>
			<Select label="From environment" bind:value={gateFrom} options={TRANSITION_OPTIONS} />
			<Select label="To environment" bind:value={gateTo} options={TRANSITION_OPTIONS} />
		</div>

		<div class="space-y-2">
			<div class="flex items-center justify-between">
				<span class="text-sm font-medium">Requirements</span>
				<Button variant="secondary" size="sm" onclick={addRequirement}>
					<Plus size={14} />Add requirement
				</Button>
			</div>

			{#each requirements as requirement, index (index)}
				<div class="space-y-3 rounded-lg border border-surface-200-800 bg-surface-100-900 p-3">
					<div class="flex items-end gap-2">
						<div class="flex-1">
							<Select
								label={`Requirement ${index + 1}`}
								bind:value={requirement.type}
								options={REQUIREMENT_OPTIONS}
							/>
						</div>
						<IconButton
							label={`Remove requirement ${index + 1}`}
							onclick={() => removeRequirement(index)}
						>
							<X size={14} />
						</IconButton>
					</div>

					{#if requirement.type === 'successful_runs'}
						<div class="grid gap-3 sm:grid-cols-3">
							<Input label="Minimum runs" type="number" min="1" bind:value={requirement.min} />
							<Input
								label="Within days"
								type="number"
								min="0"
								bind:value={requirement.withinDays}
								hint="0 = at any time"
							/>
							<Select label="Counting" bind:value={requirement.scope} options={SCOPE_OPTIONS} />
						</div>
					{:else}
						<div class="grid gap-3 sm:grid-cols-2">
							<Input label="Within days" type="number" min="1" bind:value={requirement.days} />
							<Select label="Counting" bind:value={requirement.scope} options={SCOPE_OPTIONS} />
						</div>
					{/if}
				</div>
			{:else}
				<p
					class="rounded-lg border border-dashed border-surface-300-700 px-3 py-4 text-center text-xs text-surface-600-400"
				>
					No requirements. A gate with an empty list can never be satisfied, so every matching
					promotion is refused.
				</p>
			{/each}

			<p class="text-xs text-surface-600-400">
				Every requirement must hold. Only runs that completed count, and a refusal names all the
				unmet ones at once.
			</p>
		</div>
	{/if}

	<div class="space-y-1">
		<span class="text-sm font-medium">Rule preview</span>
		<CodeEditor language="json" value={ruleJson} rows={10} readonly />
	</div>
</div>
