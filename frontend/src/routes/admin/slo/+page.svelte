<script lang="ts">
	// Service-level objectives.
	//
	// Laid out after flow-weaver's: a card per objective with a status dot, the measured
	// value, and the target underneath, then an explanation of how each is computed.
	// What flow-weaver left as an open follow-up — "override via deployment config" —
	// is here instead: the thresholds are editable in place, because an objective nobody
	// can move is an objective everybody learns to ignore.
	import { untrack } from 'svelte';
	import {
		getSlos,
		setSloTarget,
		resetSloTarget,
		type Slo,
		type SloSnapshot
	} from '$lib/api/slo.api';
	import {
		PageHeader,
		Card,
		Select,
		Button,
		Input,
		Spinner,
		ErrorState,
		Alert,
		toast
	} from '$lib/components/ui';
	import { Activity, RefreshCw, Pencil, RotateCcw, Check, X } from 'lucide-svelte';

	let days = $state('7');
	let snapshot = $state<SloSnapshot | null>(null);
	let loading = $state(true);
	let error = $state<unknown>(null);

	// Which objective is being edited, and the text in its box. Kept as a string so a
	// half-typed "0." survives a keystroke instead of collapsing to 0.
	let editing = $state<string | null>(null);
	let draft = $state('');
	let saving = $state(false);

	let seq = 0;

	async function load() {
		const mine = ++seq;
		error = null;
		try {
			const r = await getSlos(Number.parseInt(days, 10) || 7);
			if (mine !== seq) return;
			snapshot = r;
		} catch (e) {
			if (mine !== seq) return;
			if (!snapshot) error = e;
			else toast.fromError(e, "Couldn't refresh the objectives");
		} finally {
			if (mine === seq) loading = false;
		}
	}

	$effect(() => {
		days;
		untrack(() => load());
	});

	// ── formatting ────────────────────────────────────────────────────────
	//
	// A latency of 86400 s means nothing at a glance; "24 h" does. The unit the server
	// sends is the unit the number is in, and the scaling is presentation only — the
	// value that gets compared to the target is never the rounded one.
	function fmt(unit: string, value: number | null): string {
		if (value === null) return '—';
		if (unit === 'ratio') return `${(value * 100).toFixed(1)}%`;
		if (unit === 's') {
			if (value >= 86_400) return `${(value / 86_400).toFixed(1)} d`;
			if (value >= 3_600) return `${(value / 3_600).toFixed(1)} h`;
			if (value >= 60) return `${(value / 60).toFixed(1)} min`;
			return `${value.toFixed(1)} s`;
		}
		return `${value.toFixed(2)} ${unit}`;
	}

	function fmtTarget(s: Slo): string {
		return `${s.better === 'lower' ? '≤' : '≥'} ${fmt(s.unit, s.target)}`;
	}

	// Four states, not three. "No signal" is its own answer: an objective the window
	// held nothing to measure is neither met nor missed, and colouring it green would
	// claim a result nobody earned.
	type Tone = 'ok' | 'near' | 'breach' | 'none';

	function tone(s: Slo): Tone {
		if (s.value === null) return 'none';
		// The breach itself is the server's verdict, so the dot can never disagree with
		// what the daily sweep writes to the audit trail.
		if (s.breach) return 'breach';
		// Within a quarter of the threshold: still met, but close enough to say so.
		const margin = s.better === 'lower' ? s.target * 0.75 : s.target * 1.25;
		const near = s.better === 'lower' ? s.value >= margin : s.value <= margin;
		return near ? 'near' : 'ok';
	}

	const DOT: Record<Tone, string> = {
		ok: 'bg-success-500',
		near: 'bg-warning-500',
		breach: 'bg-error-500 dark:bg-error-400',
		none: 'bg-surface-400'
	};

	const VALUE: Record<Tone, string> = {
		ok: 'text-surface-950-50',
		near: 'text-warning-700-300',
		breach: 'text-error-700-300',
		none: 'text-surface-600-400'
	};

	const TONE_LABEL: Record<Tone, string> = {
		ok: 'Meeting the objective',
		near: 'Close to the threshold',
		breach: 'Missing the objective',
		none: 'Nothing to measure in this window'
	};

	// ── editing ───────────────────────────────────────────────────────────

	function startEdit(s: Slo) {
		editing = s.key;
		// Ratios are entered as percentages, because nobody thinks in 0.05.
		draft = s.unit === 'ratio' ? String(s.target * 100) : String(s.target);
	}

	function cancelEdit() {
		editing = null;
		draft = '';
	}

	async function save(s: Slo) {
		const typed = Number.parseFloat(draft.replace(',', '.'));
		if (!Number.isFinite(typed) || typed <= 0) {
			toast.error('That target is not a number above zero.');
			return;
		}
		const target = s.unit === 'ratio' ? typed / 100 : typed;
		if (s.unit === 'ratio' && target > 1) {
			toast.error('A percentage target cannot go above 100%.');
			return;
		}
		saving = true;
		try {
			await setSloTarget(s.key, target);
			cancelEdit();
			await load();
			toast.success(`${s.label} target updated`);
		} catch (e) {
			toast.fromError(e, "Couldn't save the target");
		} finally {
			saving = false;
		}
	}

	async function reset(s: Slo) {
		saving = true;
		try {
			await resetSloTarget(s.key);
			cancelEdit();
			await load();
			toast.success(`${s.label} back on its default`);
		} catch (e) {
			toast.fromError(e, "Couldn't reset the target");
		} finally {
			saving = false;
		}
	}

	const breached = $derived((snapshot?.slos ?? []).filter((s) => s.breach));
</script>

<svelte:head><title>SLOs · Admin · Nashira</title></svelte:head>

<PageHeader
	title="Service-level objectives"
	description="What the platform commits to, measured over the selected window."
>
	{#snippet actions()}
		<div class="w-36">
			<Select
				bind:value={days}
				options={[
					{ value: '1', label: 'Last day' },
					{ value: '7', label: 'Last 7 days' },
					{ value: '30', label: 'Last 30 days' },
					{ value: '90', label: 'Last 90 days' }
				]}
			/>
		</div>
		<Button variant="ghost" onclick={load}><RefreshCw size={15} />Refresh</Button>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else if loading}
	<div class="flex justify-center py-12"><Spinner size="lg" /></div>
{:else if snapshot}
	<div class="space-y-4">
		{#if breached.length > 0}
			<Alert
				tone="error"
				title={breached.length === 1
					? '1 objective is being missed'
					: `${breached.length} objectives are being missed`}
			>
				{breached.map((s) => s.label).join(', ')}. The daily sweep records each one in the
				<a class="underline" href="/admin/audit?actionPrefix=slo.breach">audit trail</a>, so the
				history survives whether or not anyone was watching.
			</Alert>
		{/if}

		<div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
			{#each snapshot.slos as s (s.key)}
				{@const t = tone(s)}
				<Card>
					<div class="flex items-start justify-between gap-2">
						<div class="flex min-w-0 items-center gap-2">
							<span
								class="h-2.5 w-2.5 shrink-0 rounded-full {DOT[t]}"
								title={TONE_LABEL[t]}
								aria-hidden="true"
							></span>
							<span class="truncate text-xs uppercase tracking-wide text-surface-600-400">
								{s.label}
							</span>
						</div>
						{#if editing !== s.key}
							<button
								type="button"
								onclick={() => startEdit(s)}
								aria-label={`Change the target for ${s.label}`}
								class="shrink-0 rounded p-1 text-surface-600-400 transition hover:bg-surface-100-900 hover:text-surface-950-50"
							>
								<Pencil size={13} />
							</button>
						{/if}
					</div>

					<div class={`mt-3 font-mono text-2xl tabular-nums ${VALUE[t]}`}>
						{fmt(s.unit, s.value)}
					</div>
					<!-- Said in words as well as colour: a status conveyed only by a coloured
					     dot is no status at all to anyone who cannot separate the hues. -->
					<div class="mt-0.5 text-[11px] text-surface-600-400">{TONE_LABEL[t]}</div>

					{#if editing === s.key}
						<div class="mt-3 space-y-2">
							<Input
								bind:value={draft}
								label={s.unit === 'ratio' ? 'Target (%)' : `Target (${s.unit})`}
								type="number"
								disabled={saving}
							/>
							<div class="flex flex-wrap gap-2">
								<Button size="sm" onclick={() => save(s)} disabled={saving}>
									<Check size={14} />Save
								</Button>
								<Button size="sm" variant="ghost" onclick={cancelEdit} disabled={saving}>
									<X size={14} />Cancel
								</Button>
								{#if s.updatedBy}
									<Button size="sm" variant="ghost" onclick={() => reset(s)} disabled={saving}>
										<RotateCcw size={14} />Default ({fmt(s.unit, s.defaultTarget)})
									</Button>
								{/if}
							</div>
						</div>
					{:else}
						<div class="mt-2 text-xs text-surface-600-400">
							target {fmtTarget(s)}
							{#if s.updatedBy}
								<span class="text-surface-700-300">
									· changed by {s.updatedBy}, default {fmt(s.unit, s.defaultTarget)}
								</span>
							{/if}
						</div>
					{/if}
				</Card>
			{/each}
		</div>

		<Card>
			<div class="mb-2 flex items-center gap-2">
				<Activity size={14} class="text-surface-600-400" />
				<h2 class="text-sm font-semibold">How these are computed</h2>
			</div>
			<dl class="space-y-2 text-sm">
				{#each snapshot.slos as s (s.key)}
					<div class="sm:flex sm:gap-3">
						<dt class="shrink-0 font-mono text-xs text-surface-700-300 sm:w-64 sm:pt-0.5">
							{s.key}
						</dt>
						<dd class="text-surface-600-400">{s.method}</dd>
					</div>
				{/each}
			</dl>
			<p class="mt-3 border-t border-surface-200-800 pt-3 text-xs text-surface-600-400">
				An objective with nothing to measure reports no value rather than a perfect score, and
				never counts as missed — otherwise a quiet week would raise an alarm about work nobody
				asked the platform to do. Window starts {snapshot.from.slice(0, 10)} UTC.
			</p>
		</Card>
	</div>
{/if}
