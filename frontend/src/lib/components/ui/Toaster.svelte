<script lang="ts">
	import { toast, type ToastTone } from './toast.svelte';
	import { CheckCircle2, AlertCircle, AlertTriangle, Info, X } from 'lucide-svelte';
	import { fly, fade } from 'svelte/transition';

	// Mount once in the root layout. Renders the transient notification stack.
	//
	// The stack lives in the top layer, not just at a high z-index. A <dialog> opened
	// with showModal() is promoted to the browser's top layer, which paints above every
	// z-index there is — so a toast fired while a modal was open (a failed save, a
	// rejected password) was drawn underneath it and effectively invisible. Promoting
	// this container with the popover API puts it in the same layer.
	//
	// It is promoted only while there are toasts to show, because the top layer stacks
	// in promotion order: a container promoted once at mount would sit *below* every
	// dialog opened afterwards, which is the bug this fixes.
	let container = $state<HTMLDivElement | undefined>(undefined);

	$effect(() => {
		const el = container;
		if (!el) return;
		const wanted = toast.items.length > 0;
		try {
			const shown = el.matches(':popover-open');
			if (wanted && !shown) el.showPopover();
			else if (!wanted && shown) el.hidePopover();
		} catch {
			// No popover support: the container stays a plain fixed layer, which is
			// what it was before — visible everywhere except above an open modal.
		}
	});

	const config: Record<
		ToastTone,
		{
			icon: typeof CheckCircle2;
			ring: string;
			bg: string;
			text: string;
			iconBg: string;
			live: 'polite' | 'assertive';
		}
	> = {
		success: {
			icon: CheckCircle2,
			ring: 'ring-success-500/30',
			bg: 'bg-surface-100-900/95',
			text: 'text-surface-900-100',
			iconBg: 'bg-success-500/15 text-success-600-400',
			live: 'polite'
		},
		error: {
			icon: AlertCircle,
			ring: 'ring-error-500/40',
			bg: 'bg-surface-100-900/95',
			text: 'text-surface-900-100',
			iconBg: 'bg-error-500/15 text-error-600-400',
			live: 'assertive'
		},
		warning: {
			icon: AlertTriangle,
			ring: 'ring-warning-500/30',
			bg: 'bg-surface-100-900/95',
			text: 'text-surface-900-100',
			iconBg: 'bg-warning-500/15 text-warning-600-400',
			live: 'polite'
		},
		info: {
			icon: Info,
			ring: 'ring-primary-500/30',
			bg: 'bg-surface-100-900/95',
			text: 'text-surface-900-100',
			iconBg: 'bg-primary-500/15 text-primary-700-300',
			live: 'polite'
		}
	};
</script>

<!-- The reset classes (inset/margin/border/background/padding/overflow) undo the UA
     styles that come with [popover]; without them the container would be centred,
     boxed and clipped instead of sitting in the corner. -->
<div
	bind:this={container}
	popover="manual"
	class="pointer-events-none fixed inset-auto bottom-4 left-auto right-4 top-auto z-[60] m-0 flex h-auto w-[min(380px,calc(100vw-2rem))] flex-col gap-2 overflow-visible border-0 bg-transparent p-0"
	aria-label="Notifications"
>
	{#each toast.items as t (t.id)}
		{@const c = config[t.tone]}
		<div
			role="status"
			aria-live={c.live}
			class="pointer-events-auto flex items-start gap-3 rounded-lg p-3.5 shadow-lg shadow-black/20 ring-1 ring-inset backdrop-blur-md {c.bg} {c.ring} {c.text}"
			in:fly={{ x: 20, duration: 200 }}
			out:fade={{ duration: 150 }}
		>
			<div class="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-md {c.iconBg}">
				<c.icon size={15} />
			</div>
			<div class="min-w-0 flex-1">
				<div class="text-sm font-medium leading-snug">{t.title}</div>
				{#if t.description}
					<div class="mt-1 text-xs leading-relaxed text-surface-600-400">{t.description}</div>
				{/if}
				{#if t.action}
					<button
						type="button"
						onclick={() => {
							t.action!.onClick();
							toast.dismiss(t.id);
						}}
						class="mt-2 inline-flex h-7 cursor-pointer items-center rounded-md bg-surface-200-800 px-2.5 text-xs font-medium text-surface-800-200 ring-1 ring-surface-300-700 transition-colors hover:bg-surface-300-700"
					>
						{t.action.label}
					</button>
				{/if}
			</div>
			<button
				type="button"
				onclick={() => toast.dismiss(t.id)}
				aria-label="Dismiss"
				class="-m-1 shrink-0 cursor-pointer rounded p-1 text-surface-600-400 transition-colors hover:bg-surface-200-800/60 hover:text-surface-800-200"
			>
				<X size={13} />
			</button>
		</div>
	{/each}
</div>
