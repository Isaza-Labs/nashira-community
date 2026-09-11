<script module lang="ts">
	import { listModels, type AiModel } from '$lib/api/models.api';

	// Module-scoped so switching threads doesn't refetch the list on every mount.
	// The set changes only when an admin edits a provider, which is rare enough
	// that a page reload is a fine way to pick it up.
	let cached: Promise<AiModel[]> | null = null;

	function loadModels(): Promise<AiModel[]> {
		cached ??= listModels().catch((e) => {
			cached = null; // a failed load must not stick for the session
			throw e;
		});
		return cached;
	}

	// providerId is a GUID, which contains no colon, so the first one separates it
	// from the model id — and model ids do contain colons (`llama3:8b`).
	function encode(m: AiModel): string {
		return `${m.providerId}:${m.id}`;
	}

	function decode(value: string): { providerId: string; model: string } | null {
		const at = value.indexOf(':');
		if (at < 1) return null;
		return { providerId: value.slice(0, at), model: value.slice(at + 1) };
	}
</script>

<script lang="ts">
	import { Cpu } from 'lucide-svelte';

	// Which provider answers this thread. `providerId`/`model` are null until the
	// server reports what it resolved (or the user picks), which is why the label
	// falls back to "Default model" rather than guessing one.
	let {
		providerId = null,
		model = null,
		providerName = null,
		onselect
	}: {
		providerId?: string | null;
		model?: string | null;
		providerName?: string | null;
		onselect: (providerId: string, model: string, providerName: string) => void;
	} = $props();

	let models = $state<AiModel[]>([]);
	let failed = $state(false);

	$effect(() => {
		let alive = true;
		loadModels()
			.then((rows) => {
				if (alive) models = rows;
			})
			.catch(() => {
				if (alive) failed = true;
			});
		return () => {
			alive = false;
		};
	});

	// Providers in the order the API returned them (by name), each keeping its own
	// model order (default first).
	const groups = $derived.by(() => {
		const byProvider = new Map<string, { name: string; models: AiModel[] }>();
		for (const m of models) {
			const g = byProvider.get(m.providerId) ?? { name: m.provider, models: [] };
			g.models.push(m);
			byProvider.set(m.providerId, g);
		}
		return [...byProvider.entries()].map(([id, g]) => ({ id, ...g }));
	});

	const selected = $derived(
		providerId && model ? models.find((m) => m.providerId === providerId && m.id === model) : undefined
	);

	// A thread can hold a provider an admin has since disabled, or a model dropped
	// from config.models. Naming it honestly beats snapping the label to something
	// the conversation is not using.
	const orphaned = $derived(!!providerId && !!model && models.length > 0 && !selected);

	const value = $derived(providerId && model ? `${providerId}:${model}` : '');

	function onChange(e: Event) {
		const parsed = decode((e.currentTarget as HTMLSelectElement).value);
		if (!parsed) return;
		const match = models.find((m) => m.providerId === parsed.providerId && m.id === parsed.model);
		onselect(parsed.providerId, parsed.model, match?.provider ?? '');
	}

	const label = $derived(
		selected
			? `${selected.provider} · ${selected.id}`
			: orphaned
				? `${providerName ?? 'Unavailable provider'} · ${model}`
				: 'Default model'
	);
</script>

{#if models.length > 0}
	<div
		class="inline-flex items-center gap-1.5 rounded-lg border border-surface-300-700 bg-surface-100-900 py-1 pl-2 pr-1 text-xs text-surface-700-300 transition focus-within:border-primary-500 hover:bg-surface-200-800 dark:focus-within:border-primary-400"
	>
		<Cpu size={13} class="shrink-0 text-surface-600-400" />
		<!-- The visible label is this span; the select sits on top of it, transparent
		     and full-width, so the control keeps native keyboard and mobile behaviour
		     while the text can be styled and truncated. -->
		<span class="relative">
			<span class="block max-w-[220px] truncate" class:text-warning-600-400={orphaned}>{label}</span>
			<select
				{value}
				onchange={onChange}
				aria-label="Model"
				title={orphaned
					? 'This conversation used a provider or model that is no longer available. Pick another to continue.'
					: 'Which AI provider answers this conversation'}
				class="absolute inset-0 w-full cursor-pointer opacity-0"
			>
				{#if !selected}
					<!-- Reachable only until the server reports what it resolved, or while
					     the stored choice is orphaned; never a thing to switch back to. -->
					<option value={value} disabled>{label}</option>
				{/if}
				{#each groups as g (g.id)}
					<optgroup label={g.name}>
						{#each g.models as m (m.id)}
							<option value={encode(m)}>{m.id}{m.isDefault ? ' (default)' : ''}</option>
						{/each}
					</optgroup>
				{/each}
			</select>
		</span>
	</div>
{:else if failed}
	<span class="text-xs text-surface-600-400">Couldn't load the model list</span>
{/if}
