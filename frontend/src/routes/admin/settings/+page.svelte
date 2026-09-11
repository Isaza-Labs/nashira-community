<script lang="ts">
	// Platform settings.
	//
	// Rendered from what the server sends, not from a form written here: each setting
	// carries its own label, description and input type, so adding one is a backend
	// change and this screen keeps working.
	//
	// The thing it is careful about is where a value came from. A setting can be showing
	// `true` because an admin chose it here, because an environment variable says so, or
	// because that is the built-in default — and only the first is editable from this
	// page in any meaningful sense. A screen that hid the distinction would leave
	// somebody typing into a field that configuration silently overrides.
	import { untrack } from 'svelte';
	import {
		getSettings,
		setSetting,
		resetSetting,
		type Setting,
		type Settings
	} from '$lib/api/settings.api';
	import {
		PageHeader,
		Card,
		Button,
		Badge,
		Input,
		Checkbox,
		Alert,
		Spinner,
		ErrorState,
		toast
	} from '$lib/components/ui';
	import { RotateCcw, Save } from 'lucide-svelte';

	let data = $state<Settings | null>(null);
	let loading = $state(true);
	let error = $state<unknown>(null);
	let saving = $state<string | null>(null);

	// Per-key text being edited. A number half-typed as "30" on the way to "300" must
	// not be parsed and clamped on every keystroke.
	let drafts = $state<Record<string, string>>({});

	async function load() {
		error = null;
		try {
			const r = await getSettings();
			data = r;
			drafts = Object.fromEntries(r.items.map((s) => [s.key, s.effective]));
		} catch (e) {
			if (!data) error = e;
			else toast.fromError(e, "Couldn't reload the settings");
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		untrack(() => load());
	});

	async function save(s: Setting, value: string) {
		saving = s.key;
		try {
			await setSetting(s.key, value);
			await load();
			toast.success(`${s.displayName} saved`);
		} catch (e) {
			// Put the field back to what the server still has, so the screen never shows
			// a value the platform is not using.
			drafts = { ...drafts, [s.key]: s.effective };
			toast.fromError(e, "Couldn't save that setting");
		} finally {
			saving = null;
		}
	}

	async function reset(s: Setting) {
		saving = s.key;
		try {
			await resetSetting(s.key);
			await load();
			toast.success(`${s.displayName} handed back to the configuration`);
		} catch (e) {
			toast.fromError(e, "Couldn't reset that setting");
		} finally {
			saving = null;
		}
	}

	const groups = $derived.by(() => {
		const out = new Map<string, Setting[]>();
		for (const s of data?.items ?? []) {
			if (!out.has(s.category)) out.set(s.category, []);
			out.get(s.category)!.push(s);
		}
		return [...out.entries()];
	});

	function sourceTone(source: string): 'primary' | 'warning' | 'neutral' {
		if (source === 'stored') return 'primary';
		// Configuration wins where nothing is stored, which is worth saying plainly:
		// editing the field will change the value, but so would a redeploy.
		if (source === 'configuration') return 'warning';
		return 'neutral';
	}

	function sourceLabel(source: string): string {
		if (source === 'stored') return 'set here';
		if (source === 'configuration') return 'from configuration';
		return 'default';
	}

	const dirty = (s: Setting) => (drafts[s.key] ?? s.effective) !== s.effective;
</script>

<svelte:head><title>Settings · Admin · Nashira</title></svelte:head>

<PageHeader
	title="Settings"
	description="Platform behaviour an admin can change without a redeploy."
/>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else if loading}
	<div class="flex justify-center py-12"><Spinner size="lg" /></div>
{:else if data}
	<div class="space-y-4">
		<Alert tone="neutral" title="How these apply">
			A change takes effect on this instance immediately and on any other within
			{data.refreshSeconds} seconds. Everything here is recorded in the
			<a class="underline" href="/admin/audit?actionPrefix=app_setting">audit trail</a> — who
			changed it, from what, to what.
		</Alert>

		{#each groups as [category, items] (category)}
			<section>
				<h2
					class="mb-2 text-xs font-semibold uppercase tracking-[0.08em] text-surface-600-400"
				>
					{category}
				</h2>
				<div class="space-y-3">
					{#each items as s (s.key)}
						<Card>
							<div class="flex flex-wrap items-start justify-between gap-3">
								<div class="min-w-0 flex-1">
									<div class="flex flex-wrap items-center gap-2">
										<span class="text-sm font-semibold text-surface-900-100">
											{s.displayName}
										</span>
										<Badge tone={sourceTone(s.source)}>{sourceLabel(s.source)}</Badge>
									</div>
									<p class="mt-1 text-xs text-surface-600-400">{s.description}</p>
									<p class="mt-1 font-mono text-[11px] text-surface-600-400">
										{s.key} · default {s.defaultValue}
									</p>
								</div>

								<div class="flex shrink-0 items-center gap-2">
									{#if s.inputType === 'bool'}
										<!-- A toggle saves on change: there is no half-typed boolean, so
										     an extra Save press would only add a way to forget. -->
										<Checkbox
											bind:checked={
												() => (drafts[s.key] ?? s.effective) === 'true',
												(v) => save(s, String(v))
											}
											disabled={saving === s.key}
											label={(drafts[s.key] ?? s.effective) === 'true' ? 'On' : 'Off'}
										/>
									{:else}
										<div class="w-40">
											<Input
												bind:value={
													() => drafts[s.key] ?? s.effective,
													(v) => (drafts = { ...drafts, [s.key]: v })
												}
												type="number"
												disabled={saving === s.key}
											/>
										</div>
										<Button
											size="sm"
											onclick={() => save(s, drafts[s.key] ?? s.effective)}
											disabled={saving === s.key || !dirty(s)}
										>
											<Save size={14} />Save
										</Button>
									{/if}

									{#if s.storedValue !== null}
										<Button
											size="sm"
											variant="ghost"
											onclick={() => reset(s)}
											disabled={saving === s.key}
											title="Hand this back to the configuration"
										>
											<RotateCcw size={14} />
										</Button>
									{/if}
								</div>
							</div>
						</Card>
					{/each}
				</div>
			</section>
		{/each}
	</div>
{/if}
