<script lang="ts">
	// The Nashira lockup — the delivered brand asset (static/Nashira_logo_v1.png:
	// serif wordmark + eight-point star, coral #EE3D23). The interface identity
	// keeps the coral version in BOTH modes (the identity PDF's light and dark
	// mockups both show the coral mark in the shell), so there is no source swap
	// on data-mode; Nashira_logo_v2.png (light on carbón) remains available for
	// contexts where coral cannot sit. The brand colour deliberately ignores
	// custom themes — it reads --color-brand, not a theme family.
	//
	// `size` keeps its old meaning of roughly the mark height; the PNG's own
	// proportions carry the wordmark. `wordmark={false}` renders just the star,
	// drawn inline so it stays crisp at icon sizes.
	let {
		size = 20,
		wordmark = true,
		class: klass = ''
	}: {
		size?: number;
		wordmark?: boolean;
		class?: string;
	} = $props();
</script>

{#if wordmark}
	<img
		src="/Nashira_logo_v1.png"
		alt="Nashira"
		draggable="false"
		style={`height: ${Math.round(size * 1.4)}px`}
		class={`w-auto select-none ${klass}`}
	/>
{:else}
	<svg
		width={size}
		height={size}
		viewBox="0 0 64 64"
		aria-hidden="true"
		class={`shrink-0 ${klass}`}
		style="color: var(--color-brand)"
	>
		<path
			fill="currentColor"
			fill-rule="evenodd"
			d="M32 2 L35.06 24.61 L46.14 17.86 L39.39 28.94 L62 32 L39.39 35.06 L46.14 46.14 L35.06 39.39 L32 62 L28.94 39.39 L17.86 46.14 L24.61 35.06 L2 32 L24.61 28.94 L17.86 17.86 L28.94 24.61 Z M32 25 L37 32 L32 39 L27 32 Z"
		/>
	</svg>
{/if}
