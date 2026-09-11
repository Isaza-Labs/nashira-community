<script lang="ts">
	// Three bouncing dots shown while the assistant reply is still empty. Pure CSS
	// (no animation library) so it stays air-gap friendly and cheap.
</script>

<span class="dots" role="status" aria-label="Assistant is thinking">
	<span></span><span></span><span></span>
</span>

<style>
	.dots {
		display: inline-flex;
		align-items: center;
		gap: 0.25rem;
		padding: 0.5rem 0.75rem;
		border-radius: 9999px;
		background: color-mix(in oklab, var(--color-surface-500) 18%, transparent);
	}
	.dots span {
		width: 0.375rem;
		height: 0.375rem;
		border-radius: 9999px;
		background: var(--color-surface-500);
		animation: chat-dot 1.2s infinite ease-in-out;
	}
	.dots span:nth-child(2) {
		animation-delay: 0.15s;
	}
	.dots span:nth-child(3) {
		animation-delay: 0.3s;
	}
	@keyframes chat-dot {
		0%,
		60%,
		100% {
			transform: translateY(0);
			opacity: 0.5;
		}
		30% {
			transform: translateY(-3px);
			opacity: 1;
		}
	}
	@media (prefers-reduced-motion: reduce) {
		.dots span {
			animation: none;
		}
	}
</style>
