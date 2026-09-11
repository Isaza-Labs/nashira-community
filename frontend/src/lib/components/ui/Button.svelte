<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { Size } from './types';
	import Spinner from './Spinner.svelte';

	type Variant = 'primary' | 'secondary' | 'ghost' | 'danger';

	let {
		variant = 'primary',
		size = 'md',
		type = 'button',
		href = undefined,
		loading = false,
		disabled = false,
		full = false,
		onclick = undefined,
		class: klass = '',
		children,
		...rest
	}: {
		variant?: Variant;
		size?: Size;
		type?: 'button' | 'submit' | 'reset';
		href?: string;
		loading?: boolean;
		disabled?: boolean;
		full?: boolean;
		onclick?: (e: MouseEvent) => void;
		class?: string;
		children?: Snippet;
		[key: string]: unknown;
	} = $props();

	// active:scale gives the press a body — the global button transition (140ms)
	// carries it, so it reads as give, not as a jump.
	const base =
		'ui-raised inline-flex items-center justify-center gap-2 font-medium transition active:scale-[0.98] disabled:opacity-60 disabled:pointer-events-none';
	const sizes: Record<Size, string> = {
		sm: 'h-8 px-3 text-sm',
		md: 'h-9 px-4 text-sm',
		lg: 'h-11 px-5 text-base'
	};
	// Filled CTAs follow the identity doc's mode pairs: azul + white in light,
	// turquesa + carbón-dark text in dark (and the coral-derived destructive
	// pair). The dark fill uses `-400` with `-950` text, which holds AA under
	// any custom theme because the engine preserves stop lightness.
	const variants: Record<Variant, string> = {
		primary:
			'bg-primary-500 text-white hover:bg-primary-600 dark:bg-primary-400 dark:text-primary-950 dark:hover:bg-primary-300',
		secondary:
			'border border-surface-300-700 bg-surface-100-900 text-surface-950-50 hover:bg-surface-200-800',
		ghost: 'text-surface-700-300 hover:bg-surface-100-900',
		danger:
			'bg-error-500 text-white hover:bg-error-600 dark:bg-error-400 dark:text-error-950 dark:hover:bg-error-300'
	};

	const cls = $derived(
		`${base} ${sizes[size]} ${variants[variant]} ${full ? 'w-full' : ''} ${klass}`
	);
</script>

{#if href}
	<a {href} class={cls} aria-disabled={disabled} {...rest}>
		{#if loading}<Spinner size="sm" />{/if}
		{@render children?.()}
	</a>
{:else}
	<button {type} class={cls} disabled={disabled || loading} {onclick} {...rest}>
		{#if loading}<Spinner size="sm" />{/if}
		{@render children?.()}
	</button>
{/if}
