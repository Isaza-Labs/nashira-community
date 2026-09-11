// Words and numbers shared by the run cards, so the list, the detail page and the
// per-workflow tab describe the same run in the same terms.

import type { Tone } from '$lib/components/ui';
import type { FleetRun } from '$lib/api/fleet.api';

const TRIGGER_LABELS: Record<string, string> = {
	manual: 'by hand',
	agent: 'the agent',
	schedule: 'a schedule',
	webhook: 'a webhook',
	git_webhook: 'a git push',
	test: 'a test',
	// A child run. Left unmapped it rendered as the bare word `subflow` beside "by hand"
	// and "a schedule", which reads like a value nobody thought about.
	subflow: 'a parent run'
};

/** "by hand", "a schedule" — what the trigger column stores, said the way a person would. */
export function triggerLabel(trigger: string): string {
	return TRIGGER_LABELS[trigger] ?? trigger;
}

export function statusTone(status: string): Tone {
	if (status === 'completed') return 'success';
	if (status === 'failed') return 'error';
	if (status === 'running') return 'primary';
	return 'neutral';
}

// The distinction the engine records and the status alone does not carry: a failed
// run that rolled everything back is contained; one that stopped halfway is not.
export function outcome(r: Pick<FleetRun, 'status' | 'finalState'>): { label: string; tone: Tone } | null {
	if (r.status !== 'failed') return null;
	if (r.finalState === 'rolled_back') return { label: 'rolled back', tone: 'warning' };
	if (r.finalState === 'failed') return { label: 'left changes behind', tone: 'error' };
	return null;
}

/** Whole-run duration from the server's seconds. "running" while there is no end. */
export function runDuration(seconds: number | null): string {
	if (seconds === null) return 'running';
	if (seconds < 60) return `${seconds}s`;
	if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
	return `${(seconds / 3600).toFixed(1)}h`;
}

/** Per-step duration, from milliseconds. */
export function stepDuration(ms: number | null): string | null {
	if (ms === null || ms === undefined) return null;
	if (ms < 1000) return `${ms}ms`;
	if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
	return `${Math.floor(ms / 60_000)}m ${Math.round((ms % 60_000) / 1000)}s`;
}

/** "1.2 MB" for a payload size in characters — what a click is about to fetch. */
export function payloadSize(chars: number): string {
	if (chars < 1024) return `${chars} B`;
	if (chars < 1024 * 1024) return `${(chars / 1024).toFixed(chars < 10 * 1024 ? 1 : 0)} KB`;
	return `${(chars / (1024 * 1024)).toFixed(1)} MB`;
}

/** The result word of a step, as a tone for the data-flow chip and the badge. */
export function stepTone(result: string): Tone {
	switch (result) {
		case 'changed':
			return 'success';
		case 'failed':
			return 'error';
		case 'skipped':
			return 'warning';
		default:
			return 'neutral';
	}
}

// Cap on what one block puts in the DOM. A per-device node can return megabytes,
// and laying out all of it before anything appears is the delay this page exists
// to remove.
const RENDER_LIMIT = 200_000;

export function clip(text: string): string {
	if (text.length <= RENDER_LIMIT) return text;
	return (
		text.slice(0, RENDER_LIMIT) +
		`\n… truncated for display — ${text.length.toLocaleString()} characters total`
	);
}

export function pretty(value: unknown): string {
	try {
		return clip(JSON.stringify(value, null, 2));
	} catch {
		return clip(String(value));
	}
}

export function shortId(id: string | undefined | null): string {
	return (id ?? '').slice(0, 8);
}
