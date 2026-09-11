<script lang="ts">
	import { prefs } from '$lib/stores/prefs.svelte';
	import { themeStyle, type ThemeSettings, type UiMode } from '$lib/stores/theme.svelte';

	// A real slice of the interface, painted with a theme's tokens instead of the
	// live ones. Colour swatches answer "what hue did I pick"; this answers the
	// question people actually have — "what will the app look like".
	//
	// It works by scoping the theme's custom properties to this wrapper: every
	// utility inside resolves `var(--color-…)` against these values, so the
	// buttons, badges and inputs below are the same components the app uses, not
	// drawings of them.
	//
	// Style settings ride along the same way. The corner radii need nothing extra
	// — `.ui-surface` and the `rounded-*` utilities already resolve the tokens
	// this wrapper redefines — but the font stacks do: `font-family` is inherited
	// from <html>, where it was already resolved against the LIVE `--font-sans`,
	// so the wrapper has to re-anchor it or the preview would show the running
	// theme's typeface while claiming to show this one's.
	let {
		colors,
		settings = undefined,
		mode = undefined,
		compact = false
	}: {
		colors: Record<string, string>;
		settings?: ThemeSettings;
		/** Defaults to the mode the user is actually in. */
		mode?: UiMode;
		compact?: boolean;
	} = $props();

	const resolvedMode = $derived<UiMode>(mode ?? prefs.mode);
	// `--app-bg` is only in the map when the theme tints surface; painting the
	// frame with the variable means an untinted theme still shows the stock shell.
	const style = $derived(
		`${themeStyle(colors, resolvedMode, settings)}; background: var(--app-bg); font-family: var(--font-sans)`
	);
</script>

<div {style} aria-hidden="true" class="overflow-hidden rounded-lg border border-surface-200-800">
	<div class={compact ? 'space-y-2 p-2.5' : 'space-y-3 p-4'}>
		<div class="ui-surface {compact ? 'space-y-2 p-2.5' : 'space-y-3 p-3'}">
			<div class="flex items-center justify-between gap-2">
				<!-- Deliberately styled through the heading tokens rather than a
				     font-semibold utility: it is the only place the heading font
				     and weight knobs become visible before saving. -->
				<span
					class="text-xs text-surface-900-100"
					style="font-family: var(--heading-font-family, inherit); font-weight: var(--heading-font-weight, 650)"
				>
					Device sync
				</span>
				<span
					class="inline-flex items-center rounded-full bg-success-500/15 px-1.5 py-0.5 text-[10px] font-medium text-success-700-300"
				>
					healthy
				</span>
			</div>

			<div class="flex flex-wrap items-center gap-1.5">
				<!-- Filled CTAs switch fills per mode in the real components (see
				     Button.svelte). The preview keys off ITS OWN mode, not the
				     app's, so a `dark:` utility (which reads <html data-mode>)
				     would lie here — the pair is chosen explicitly instead. -->
				<span
					class="rounded-md px-2 py-1 text-[10px] font-medium {resolvedMode === 'dark'
						? 'bg-primary-400 text-primary-950'
						: 'bg-primary-500 text-white'}"
				>
					Run
				</span>
				<span
					class="rounded-md border border-surface-300-700 bg-surface-100-900 px-2 py-1 text-[10px] font-medium text-surface-950-50"
				>
					Simulate
				</span>
				<span class="rounded-md px-2 py-1 text-[10px] font-medium text-surface-700-300">Cancel</span>
				<span
					class="rounded-md px-2 py-1 text-[10px] font-medium {resolvedMode === 'dark'
						? 'bg-error-400 text-error-950'
						: 'bg-error-500 text-white'}"
				>
					Delete
				</span>
			</div>

			{#if !compact}
				<div
					class="ui-control flex items-center px-2 py-1.5 font-mono text-[11px] text-surface-600-400"
					role="presentation"
				>
					core-router-01 · 10.20.0.1
				</div>

				<div class="space-y-1">
					<div class="flex items-center justify-between border-t border-surface-200-800 pt-1.5">
						<span class="text-[11px] text-surface-700-300">edge-sw-04</span>
						<span
							class="inline-flex items-center rounded-full bg-warning-500/15 px-1.5 py-0.5 text-[10px] font-medium text-warning-700-300"
						>
							degraded
						</span>
					</div>
					<div class="flex items-center justify-between">
						<span class="text-[11px] text-surface-700-300">lab-fw-02</span>
						<span
							class="inline-flex items-center rounded-full bg-error-500/15 px-1.5 py-0.5 text-[10px] font-medium text-error-700-300"
						>
							unreachable
						</span>
					</div>
				</div>

				<span class="block text-[11px] font-medium text-primary-700-300 underline">
					Open the run log
				</span>
			{/if}
		</div>

		{#if !compact}
			<div class="flex gap-1">
				{#each [100, 300, 500, 700, 900] as stop (stop)}
					<span
						class="h-4 flex-1 rounded"
						style:background={`var(--color-primary-${stop})`}
					></span>
				{/each}
			</div>
		{/if}
	</div>
</div>
