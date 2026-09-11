<script lang="ts">
	// Theme studio. A theme is a DIFF, not a repaint: it overrides the colour
	// families it enables and the style knobs it moves, and everything it leaves
	// alone keeps coming from app.css. That is what makes a one-family theme — or
	// a settings-only one — a sensible thing to save.
	//
	// The editor has two halves. Colours go through the engine's ramp generator,
	// which keeps each stop's contrast-checked lightness and swaps the hue. Style
	// settings (roundness, interface scale, fonts, heading weight) map onto
	// Nashira's own tokens: `--srf-radius` / `--ctl-radius` / `--btn-radius` plus
	// Tailwind's `--radius-*`, and `--font-sans` / `--font-mono` /
	// `--heading-font-family` / `--heading-font-weight`.
	import {
		listThemes,
		createTheme,
		updateTheme,
		deleteTheme,
		type Theme,
		type ThemePayload
	} from '$lib/api/themes.api';
	import {
		themeEngine,
		THEME_FAMILIES,
		stockHex,
		SETTING_DEFAULTS,
		FONT_BODY_KEYS,
		FONT_HEADING_KEYS,
		FONT_MONO_KEYS,
		type FontBodyKey,
		type FontHeadingKey,
		type FontMonoKey,
		type ThemeFamily,
		type ThemeSettings
	} from '$lib/stores/theme.svelte';
	import {
		FAMILY_META,
		FONT_BODY_LABEL,
		FONT_HEADING_LABEL,
		FONT_MONO_LABEL,
		HEADING_WEIGHTS,
		THEME_PRESETS,
		pruneSettings,
		randomThemeColors,
		type ThemePreset
	} from '$lib/theme/presets';
	import { saveBlob } from '$lib/utils/download';
	import { authStore } from '$lib/stores/auth.svelte';
	import { prefs } from '$lib/stores/prefs.svelte';
	import ThemePreview from '$lib/components/theme/ThemePreview.svelte';
	import SettingSlider from '$lib/components/theme/SettingSlider.svelte';
	import {
		PageHeader,
		Button,
		Modal,
		Badge,
		IconButton,
		Input,
		Textarea,
		Select,
		Checkbox,
		Tabs,
		Alert,
		Spinner,
		ErrorState,
		confirm,
		toast
	} from '$lib/components/ui';
	import {
		Plus,
		Pencil,
		Trash2,
		Check,
		RotateCcw,
		CopyPlus,
		Download,
		Upload,
		ClipboardPaste,
		Dices
	} from 'lucide-svelte';

	let items = $state<Theme[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<Theme | null>(null);
	let saving = $state(false);
	let formError = $state('');
	let editorTab = $state('colors');

	const isAdmin = $derived((authStore.session?.role ?? '') === 'admin');

	function blankColors(): Record<ThemeFamily, string> {
		return Object.fromEntries(THEME_FAMILIES.map((f) => [f, stockHex(f)])) as Record<
			ThemeFamily,
			string
		>;
	}

	function noFamilies(): Record<ThemeFamily, boolean> {
		return Object.fromEntries(THEME_FAMILIES.map((f) => [f, false])) as Record<ThemeFamily, boolean>;
	}

	let fName = $state('');
	let fDescription = $state('');
	let fShared = $state(false);
	// Which families this theme overrides, and with what. A family left disabled
	// falls back to the stock ramp — a theme is a diff, not a full repaint.
	let fEnabled = $state<Record<ThemeFamily, boolean>>({ ...noFamilies(), primary: true });
	let fColors = $state<Record<ThemeFamily, string>>(blankColors());
	// Materialised with every default so each control has something to bind to;
	// only the knobs that actually differ get saved (see draftSettings). The
	// distinction matters: an absent key keeps inheriting app.css, while a saved
	// one pins today's default and stops tracking it.
	let fSettings = $state<Required<ThemeSettings>>({ ...SETTING_DEFAULTS });

	// Preview chips come from the SAME ramp function apply() uses, so the preview
	// is by construction what applying would do.
	const previewStops = [100, 300, 500, 700, 900];
	function previewRamp(family: ThemeFamily): string[] {
		const ramp = themeEngine.ramp(family, fColors[family]);
		if (!ramp) return [];
		return previewStops.map((s) => ramp[s]).filter(Boolean);
	}

	// What the editor would save right now — drives the live component preview.
	const draftColors = $derived(
		Object.fromEntries(
			THEME_FAMILIES.filter((f) => fEnabled[f]).map((f) => [f, fColors[f]])
		) as Record<string, string>
	);
	const draftSettings = $derived(pruneSettings(fSettings));
	const settingCount = $derived(Object.keys(draftSettings).length);

	// The preview is shown in both modes on request: a theme that reads well in
	// dark and falls apart in light is a theme you want to find out about here.
	let previewMode = $state<'live' | 'light' | 'dark'>('live');

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listThemes()).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	function resetForm() {
		formError = '';
		editorTab = 'colors';
		previewMode = 'live';
		fEnabled = { ...noFamilies(), primary: true };
		fColors = blankColors();
		fSettings = { ...SETTING_DEFAULTS };
	}

	function openCreate() {
		editing = null;
		resetForm();
		fName = '';
		fDescription = '';
		fShared = false;
		modalOpen = true;
	}

	function loadInto(t: Theme) {
		editing = t;
		resetForm();
		fName = t.name;
		fDescription = t.description ?? '';
		fShared = t.isShared;
		for (const family of THEME_FAMILIES) {
			const hex = t.colors[family];
			fEnabled[family] = typeof hex === 'string' && /^#?[0-9a-f]{6}$/i.test(hex);
			if (fEnabled[family]) fColors[family] = hex.startsWith('#') ? hex : `#${hex}`;
			else fColors[family] = stockHex(family);
		}
		fSettings = { ...SETTING_DEFAULTS, ...t.settings };
	}

	function openEdit(t: Theme) {
		loadInto(t);
		modalOpen = true;
	}

	// Keeps what is in the form but detaches it from the saved row, so saving
	// creates a new private theme. This is how anyone starts from a shared theme —
	// including the people who are not allowed to change that one.
	function detachAsCopy() {
		editing = null;
		if (fName.trim()) fName = `${fName.trim()} (copy)`;
		fShared = false;
		formError = '';
		toast.success('Editing a copy — save it to keep it');
	}

	function duplicate(t: Theme) {
		loadInto(t);
		detachAsCopy();
		modalOpen = true;
	}

	function applyPreset(preset: ThemePreset) {
		for (const family of THEME_FAMILIES) {
			const hex = preset.colors[family];
			fEnabled[family] = hex !== undefined;
			if (hex) fColors[family] = hex;
		}
		fSettings = { ...SETTING_DEFAULTS, ...(preset.settings ?? {}) };
	}

	function surpriseMe() {
		const rolled = randomThemeColors();
		for (const family of THEME_FAMILIES) {
			fEnabled[family] = true;
			fColors[family] = rolled[family];
		}
	}

	function selectedColors(): Record<string, string> {
		const colors: Record<string, string> = {};
		for (const family of THEME_FAMILIES) {
			if (fEnabled[family]) colors[family] = fColors[family];
		}
		return colors;
	}

	async function save() {
		if (!fName.trim()) {
			formError = 'Name is required.';
			return;
		}
		const colors = selectedColors();
		if (Object.keys(colors).length === 0 && settingCount === 0) {
			formError =
				'Enable a color family or move a style setting — a theme that overrides nothing changes nothing.';
			return;
		}
		formError = '';
		saving = true;
		const payload: ThemePayload = {
			name: fName.trim(),
			description: fDescription,
			colors,
			settings: draftSettings,
			isShared: fShared
		};
		try {
			const saved = editing ? await updateTheme(editing.id, payload) : await createTheme(payload);
			modalOpen = false;
			toast.success(editing ? 'Theme updated' : 'Theme created');
			// Editing the currently applied theme re-applies it, so the screen
			// matches what was just saved instead of the pre-edit colors.
			if (themeEngine.activeId === saved.id)
				themeEngine.apply(saved.id, saved.colors, saved.settings);
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the theme');
		} finally {
			saving = false;
		}
	}

	function applyTheme(t: Theme) {
		themeEngine.apply(t.id, t.colors, t.settings);
		toast.success(`Theme "${t.name}" applied`);
	}

	function resetTheme() {
		themeEngine.reset();
		toast.success('Back to the stock look');
	}

	async function remove(t: Theme) {
		const ok = await confirm({
			title: 'Delete theme?',
			message: t.isShared
				? `"${t.name}" is shared — deleting it removes it for every user.`
				: `"${t.name}" will be removed.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteTheme(t.id);
			if (themeEngine.activeId === t.id) themeEngine.reset();
			items = items.filter((x) => x.id !== t.id);
			toast.success('Theme deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the theme");
		}
	}

	// Which families a stored theme actually overrides — the rest fall back to the
	// stock ramp, and saying so is more useful than showing five identical chips.
	function overridden(t: Theme): ThemeFamily[] {
		return THEME_FAMILIES.filter((f) => typeof t.colors[f] === 'string');
	}

	const SETTING_LABEL: Record<keyof ThemeSettings, string> = {
		roundness: 'corners',
		ui_scale: 'scale',
		font_body: 'body font',
		font_heading: 'heading font',
		font_mono: 'code font',
		heading_weight: 'heading weight'
	};

	function overriddenSettings(t: Theme): string[] {
		return Object.keys(t.settings).map((k) => SETTING_LABEL[k as keyof ThemeSettings] ?? k);
	}

	function canEdit(t: Theme): boolean {
		return t.isShared ? isAdmin : t.isMine || isAdmin;
	}

	// ── import / export ─────────────────────────────────────────────────
	// A theme as portable JSON. The point is moving one between accounts or
	// instances without a database, so the shape is the API's, not a private one.

	let transferOpen = $state(false);
	let importFile = $state<HTMLInputElement | undefined>(undefined);
	let transferText = $state('');
	let transferError = $state('');

	// A file, not the clipboard. The clipboard path was one keystroke away from
	// losing the whole theme — anything copied afterwards overwrote it — and on the
	// LAN deployments this runs on (http://<ip>:3006) navigator.clipboard does not
	// exist at all, so half the time "Export" ended in the paste-it-back-yourself
	// fallback. A download survives the session and can be mailed or committed.
	function exportTheme() {
		const name = fName.trim() || 'Untitled theme';
		const payload: Record<string, unknown> = { name, colors: selectedColors() };
		if (fDescription.trim()) payload.description = fDescription.trim();
		if (settingCount > 0) payload.settings = draftSettings;
		const json = JSON.stringify(payload, null, 2);

		saveBlob(new Blob([json], { type: 'application/json' }), `${fileSlug(name)}.theme.json`);
		toast.success(`Exported ${fileSlug(name)}.theme.json`);
	}

	// The theme name as a file name. Everything outside [a-z0-9-] goes, because the
	// name is free text a user typed and it lands on a file system.
	function fileSlug(name: string): string {
		const s = name
			.toLowerCase()
			.normalize('NFD')
			.replace(/[\u0300-\u036f]/g, '')
			.replace(/[^a-z0-9]+/g, '-')
			.replace(/^-+|-+$/g, '');
		return s || 'theme';
	}

	// Reading an exported file back. The import dialog still accepts a paste — that
	// is how a theme arrives from a chat message — but a file is now the shape
	// Export produces, so it has to be the shape Import takes.
	async function onImportFile(e: Event) {
		const input = e.target as HTMLInputElement;
		const file = input.files?.[0];
		input.value = ''; // let the same file be picked again after a failed parse
		if (!file) return;
		try {
			transferText = await file.text();
			transferError = '';
		} catch {
			transferError = `Couldn't read "${file.name}".`;
		}
	}

	function openImport() {
		transferText = '';
		transferError = '';
		transferOpen = true;
	}

	// Validated here as well as on the server. This runs before anything is saved,
	// so a bad paste can say what is wrong with it instead of becoming a 400 after
	// the user has already committed to it.
	function runImport() {
		transferError = '';
		let parsed: unknown;
		try {
			parsed = JSON.parse(transferText);
		} catch {
			transferError = 'Not valid JSON.';
			return;
		}
		if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
			transferError = 'Expected an object like { "name": …, "colors": { … } }.';
			return;
		}
		const raw = parsed as Record<string, unknown>;

		const nextColors = blankColors();
		const nextEnabled = noFamilies();
		let colorCount = 0;
		if (raw.colors !== undefined) {
			if (typeof raw.colors !== 'object' || raw.colors === null || Array.isArray(raw.colors)) {
				transferError = '"colors" must be an object of family → hex.';
				return;
			}
			for (const [key, value] of Object.entries(raw.colors)) {
				if (!(THEME_FAMILIES as readonly string[]).includes(key)) {
					transferError = `Unknown color family "${key}". Allowed: ${THEME_FAMILIES.join(', ')}.`;
					return;
				}
				if (typeof value !== 'string' || !/^#?[0-9a-f]{6}$/i.test(value)) {
					transferError = `colors.${key} must be a 6-digit hex like #2b7d95.`;
					return;
				}
				nextEnabled[key as ThemeFamily] = true;
				nextColors[key as ThemeFamily] = value.startsWith('#') ? value : `#${value}`;
				colorCount += 1;
			}
		}

		const nextSettings: Record<string, unknown> = {};
		if (raw.settings !== undefined) {
			if (typeof raw.settings !== 'object' || raw.settings === null || Array.isArray(raw.settings)) {
				transferError = '"settings" must be an object.';
				return;
			}
			for (const [key, value] of Object.entries(raw.settings)) {
				const ok =
					(key === 'roundness' && typeof value === 'number' && value >= 0 && value <= 2) ||
					(key === 'ui_scale' && typeof value === 'number' && value >= 0.85 && value <= 1.15) ||
					(key === 'heading_weight' && typeof value === 'number' && value >= 300 && value <= 900) ||
					(key === 'font_body' && (FONT_BODY_KEYS as readonly unknown[]).includes(value)) ||
					(key === 'font_heading' && (FONT_HEADING_KEYS as readonly unknown[]).includes(value)) ||
					(key === 'font_mono' && (FONT_MONO_KEYS as readonly unknown[]).includes(value));
				if (!ok) {
					transferError = `settings.${key} is not a setting this app knows, or its value is out of range.`;
					return;
				}
				nextSettings[key] = value;
			}
		}

		if (colorCount === 0 && Object.keys(nextSettings).length === 0) {
			transferError = 'That theme overrides nothing.';
			return;
		}

		// Loads as a NEW theme. Importing over somebody's saved row by accident
		// would be far worse than having to reopen it afterwards.
		editing = null;
		resetForm();
		fName = typeof raw.name === 'string' ? raw.name.slice(0, 80) : '';
		fDescription = typeof raw.description === 'string' ? raw.description.slice(0, 300) : '';
		fEnabled = nextEnabled;
		fColors = nextColors;
		fSettings = { ...SETTING_DEFAULTS, ...(nextSettings as ThemeSettings) };
		fShared = false;
		transferOpen = false;
		transferText = '';
		modalOpen = true;
		toast.success('Theme loaded into the editor — save it to keep it');
	}
</script>

<svelte:head><title>Themes · Nashira</title></svelte:head>

<PageHeader
	title="Themes"
	description="Recolor and reshape the interface without breaking its contrast."
>
	{#snippet actions()}
		{#if themeEngine.activeId}
			<Button variant="secondary" onclick={resetTheme}><RotateCcw size={15} />Stock look</Button>
		{/if}
		<Button variant="ghost" onclick={openImport}><ClipboardPaste size={15} />Import</Button>
		<Button variant="primary" onclick={openCreate}><Plus size={15} />New theme</Button>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else if loading}
	<div class="flex justify-center py-16"><Spinner size="lg" /></div>
{:else}
	<div class="space-y-3">
		<Alert tone="neutral">
			A theme picks a base color per family; the engine keeps each shade's original lightness and
			swaps the hue, so the contrast the stock palette guarantees survives recoloring. Tinting
			<strong>surface</strong> also moves the app background and the controls, and the
			<strong>style</strong> settings reshape the corners, the type and the size of the whole
			interface. Themes apply per browser and follow you across light and dark mode.
		</Alert>

		{#if items.length === 0}
			<div
				class="rounded-xl border border-surface-200-800 px-4 py-16 text-center text-sm text-surface-600-400"
			>
				No themes yet. Create one — the stock look stays a click away.
			</div>
		{:else}
			<div class="grid gap-3 sm:grid-cols-2">
				{#each items as t (t.id)}
					<div class="ui-surface p-4">
						<div class="flex items-start gap-3">
							<div class="min-w-0 flex-1">
								<div class="flex flex-wrap items-center gap-2">
									<span class="font-semibold">{t.name}</span>
									{#if themeEngine.activeId === t.id}
										<Badge tone="primary">active</Badge>
									{/if}
									{#if t.isShared}<Badge>shared</Badge>{/if}
									{#if !t.isMine && !t.isShared}<Badge tone="neutral">other user</Badge>{/if}
								</div>
								{#if t.description}
									<p class="mt-0.5 text-xs text-surface-600-400">{t.description}</p>
								{/if}
							</div>

							<div class="flex flex-col gap-1">
								{#if themeEngine.activeId !== t.id}
									<IconButton label={`Apply ${t.name}`} onclick={() => applyTheme(t)}>
										<Check size={14} />
									</IconButton>
								{/if}
								<!-- Offered on every theme, including the shared ones only an admin
								     may edit: duplicating is how anyone starts from one. -->
								<IconButton label={`Duplicate ${t.name}`} onclick={() => duplicate(t)}>
									<CopyPlus size={14} />
								</IconButton>
								{#if canEdit(t)}
									<IconButton label="Edit theme" onclick={() => openEdit(t)}>
										<Pencil size={14} />
									</IconButton>
									<IconButton label="Delete theme" onclick={() => remove(t)}>
										<Trash2 size={14} />
									</IconButton>
								{/if}
							</div>
						</div>

						<div class="mt-3">
							<ThemePreview colors={t.colors} settings={t.settings} compact />
						</div>

						<div class="mt-2 flex flex-wrap gap-1">
							{#each overridden(t) as family (family)}
								<span
									class="rounded bg-surface-200-800 px-1.5 py-0.5 text-[10px] text-surface-700-300"
								>
									{FAMILY_META[family].label}
								</span>
							{/each}
							{#each overriddenSettings(t) as label (label)}
								<span
									class="rounded bg-primary-500/15 px-1.5 py-0.5 text-[10px] text-primary-700-300"
								>
									{label}
								</span>
							{/each}
						</div>
					</div>
				{/each}
			</div>
		{/if}
	</div>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit theme' : 'New theme'} size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		{#if editing && !canEdit(editing)}
			<Alert tone="warning">
				This is a shared theme published by somebody else. Saving would be refused — duplicate it,
				and the copy is yours.
			</Alert>
		{/if}

		<Input label="Name" bind:value={fName} required />
		<Textarea label="Description" bind:value={fDescription} rows={2} />

		<div class="space-y-2">
			<div class="flex items-center justify-between">
				<span class="text-sm font-medium">Preview</span>
				<div class="flex gap-1 text-xs">
					{#each [['live', prefs.mode === 'dark' ? 'Dark (yours)' : 'Light (yours)'], ['light', 'Light'], ['dark', 'Dark']] as [value, label] (value)}
						<button
							type="button"
							onclick={() => (previewMode = value as typeof previewMode)}
							class="rounded-md px-2 py-1 transition {previewMode === value
								? 'bg-primary-500/15 font-medium text-primary-700-300'
								: 'text-surface-600-400 hover:bg-surface-100-900'}"
						>
							{label}
						</button>
					{/each}
				</div>
			</div>

			{#if Object.keys(draftColors).length === 0 && settingCount === 0}
				<div
					class="rounded-lg border border-dashed border-surface-300-700 px-4 py-6 text-center text-xs text-surface-600-400"
				>
					Enable a family or move a style setting to see it here.
				</div>
			{:else}
				<ThemePreview
					colors={draftColors}
					settings={draftSettings}
					mode={previewMode === 'live' ? undefined : previewMode}
				/>
			{/if}
		</div>

		<Tabs
			bind:value={editorTab}
			tabs={[
				{ value: 'colors', label: 'Colors' },
				{ value: 'style', label: `Style${settingCount > 0 ? ` (${settingCount})` : ''}` }
			]}
		/>

		{#if editorTab === 'colors'}
			<div>
				<div class="mb-1.5 text-xs font-medium text-surface-600-400">Start from</div>
				<div class="flex flex-wrap gap-1.5">
					{#each THEME_PRESETS as preset (preset.key)}
						<button
							type="button"
							onclick={() => applyPreset(preset)}
							title={preset.hint}
							class="inline-flex items-center gap-1.5 rounded-md border border-surface-300-700 py-1 pl-1.5 pr-2 text-xs text-surface-700-300 transition hover:bg-surface-100-900 hover:text-surface-950-50"
						>
							<span class="inline-flex overflow-hidden rounded-sm">
								{#each ['primary', 'secondary', 'surface'] as const as p (p)}
									<span class="h-3 w-2" style={`background: ${preset.colors[p]}`}></span>
								{/each}
							</span>
							{preset.label}
						</button>
					{/each}
					<button
						type="button"
						onclick={surpriseMe}
						title="Roll a random palette that still reads as one family"
						class="inline-flex items-center gap-1.5 rounded-md border border-surface-300-700 px-2 py-1 text-xs text-surface-700-300 transition hover:bg-surface-100-900 hover:text-surface-950-50"
					>
						<Dices size={13} /> Surprise me
					</button>
				</div>
			</div>

			<div class="space-y-2">
				{#each THEME_FAMILIES as family (family)}
					<div class="rounded-lg border border-surface-200-800 p-3">
						<div class="flex flex-wrap items-center gap-3">
							<label class="inline-flex min-w-40 items-center gap-2 text-sm font-medium">
								<input
									type="checkbox"
									bind:checked={fEnabled[family]}
									class="h-4 w-4 rounded border-surface-300-700 accent-primary-500"
								/>
								{FAMILY_META[family].label}
							</label>

							{#if fEnabled[family]}
								<input
									type="color"
									bind:value={fColors[family]}
									aria-label={`${FAMILY_META[family].label} base color`}
									class="h-8 w-12 cursor-pointer rounded border border-surface-300-700 bg-transparent"
								/>
								<div class="flex gap-1">
									{#each previewRamp(family) as chip (chip)}
										<span class="h-6 w-6 rounded" style={`background: ${chip}`}></span>
									{/each}
								</div>
							{:else}
								<span class="text-xs text-surface-600-400">stock ramp</span>
							{/if}
						</div>
						{#if fEnabled[family]}
							<p class="mt-1 text-xs text-surface-600-400">{FAMILY_META[family].hint}</p>
						{/if}
					</div>
				{/each}
			</div>

			{#if fEnabled.surface}
				<Alert tone="warning">
					The surface tint recolors the neutral base of every screen — cards, inputs, borders and the
					app background itself. The engine keeps the tint subtle, but small hue shifts go a long way
					here.
				</Alert>
			{/if}
		{:else}
			<div class="space-y-1">
				<p class="text-xs text-surface-600-400">
					Every knob starts on the shipped value, and one left there is not saved at all — so it
					keeps following app.css if the design system moves.
				</p>

				<div class="divide-y divide-surface-200-800">
					<SettingSlider
						label="Corner roundness"
						hint="Scales every radius: cards, controls, buttons, badges. 0 is square."
						min={0}
						max={2}
						step={0.05}
						fallback={SETTING_DEFAULTS.roundness}
						format={(v) => `${v.toFixed(2)}×`}
						bind:value={fSettings.roundness}
					/>
					<SettingSlider
						label="Interface scale"
						hint="Moves the root font size — text and rem-based spacing follow together."
						min={0.85}
						max={1.15}
						step={0.01}
						fallback={SETTING_DEFAULTS.ui_scale}
						format={(v) => `${Math.round(v * 100)}%`}
						bind:value={fSettings.ui_scale}
					/>
				</div>

				<div class="grid gap-3 pt-2 sm:grid-cols-2">
					<Select
						label="Body font"
						hint="Only stacks the app already bundles or the system already has."
						options={FONT_BODY_KEYS.map((k) => ({ value: k, label: FONT_BODY_LABEL[k] }))}
						bind:value={() => fSettings.font_body, (v) => (fSettings.font_body = v as FontBodyKey)}
					/>
					<Select
						label="Heading font"
						options={FONT_HEADING_KEYS.map((k) => ({ value: k, label: FONT_HEADING_LABEL[k] }))}
						bind:value={
							() => fSettings.font_heading, (v) => (fSettings.font_heading = v as FontHeadingKey)
						}
					/>
					<Select
						label="Heading weight"
						options={HEADING_WEIGHTS.map((w) => ({
							value: String(w),
							label: w === SETTING_DEFAULTS.heading_weight ? `${w} (default)` : String(w)
						}))}
						bind:value={
							() => String(fSettings.heading_weight),
							(v) => (fSettings.heading_weight = Number(v))
						}
					/>
					<Select
						label="Code font"
						hint="IPs, hostnames, CLI output and diffs."
						options={FONT_MONO_KEYS.map((k) => ({ value: k, label: FONT_MONO_LABEL[k] }))}
						bind:value={() => fSettings.font_mono, (v) => (fSettings.font_mono = v as FontMonoKey)}
					/>
				</div>

				<p class="pt-1 text-xs text-surface-600-400">
					Interface scale is the one setting the preview can only hint at: the miniature's text is
					sized in rem, which resolves against the real page rather than this pane.
				</p>
			</div>
		{/if}

		{#if isAdmin}
			<Checkbox bind:checked={fShared} label="Share with every user" />
			{#if fShared}
				<Alert tone="neutral">
					A shared theme appears in everyone's list, and from then on only an administrator can
					change or delete it — including you.
				</Alert>
			{/if}
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={exportTheme}><Download size={15} />Export JSON</Button>
		{#if editing}
			<Button variant="ghost" onclick={detachAsCopy}><CopyPlus size={15} />Duplicate</Button>
		{/if}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>

<Modal bind:open={transferOpen} title="Import a theme" size="lg">
	<div class="space-y-2">
		<p class="text-xs text-surface-600-400">
			Choose an exported <code class="font-mono">.theme.json</code> file, or paste the JSON below —
			the same shape Export produces. It loads into the editor as a new theme; nothing is saved
			until you save it.
		</p>
		<div>
			<input
				bind:this={importFile}
				type="file"
				accept="application/json,.json"
				class="hidden"
				onchange={onImportFile}
			/>
			<Button size="sm" variant="ghost" onclick={() => importFile?.click()}>
				<Upload size={14} />Choose file…
			</Button>
		</div>
		<Textarea
			bind:value={transferText}
			rows={10}
			spellcheck="false"
			placeholder={'{\n  "name": "Midnight ops",\n  "colors": { "primary": "#506eb0", "surface": "#717682" },\n  "settings": { "roundness": 0.5 }\n}'}
		/>
		{#if transferError}<Alert tone="error">{transferError}</Alert>{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (transferOpen = false)}>Cancel</Button>
		<Button variant="primary" onclick={runImport} disabled={!transferText.trim()}>
			Load into editor
		</Button>
	{/snippet}
</Modal>
