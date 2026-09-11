// Service-level objectives (`/api/admin/slo`, Admin).
//
// `value` is null when the window held nothing to measure. That is not zero and must
// not be drawn as a score — an objective with no signal is neither met nor missed.

import { api } from '$lib/api/client';

export type Slo = {
	key: string;
	label: string;
	unit: string;
	/** The threshold in force — the built-in one unless somebody moved it. */
	target: number;
	defaultTarget: number;
	value: number | null;
	/** 'lower' or 'higher' — which direction meets the objective. */
	better: 'lower' | 'higher' | string;
	method: string;
	/** Decided by the server, so the screen and the breach alerts agree. */
	breach: boolean;
	/** Null while the built-in target still applies. */
	updatedBy: string | null;
};

export type SloSnapshot = {
	days: number;
	from: string;
	slos: Slo[];
};

interface SloShape {
	key: string;
	label: string;
	unit: string;
	target: number;
	default_target: number;
	value: number | null;
	better: string;
	method: string;
	breach: boolean;
	updated_by: string | null;
}

function toSlo(s: SloShape): Slo {
	return {
		key: s.key,
		label: s.label,
		unit: s.unit,
		target: s.target,
		defaultTarget: s.default_target,
		value: s.value,
		better: s.better,
		method: s.method,
		breach: s.breach,
		updatedBy: s.updated_by
	};
}

export async function getSlos(days: number): Promise<SloSnapshot> {
	const r = await api<{ days: number; from: string; slos: SloShape[] }>(
		`/admin/slo?days=${days}`
	);
	return { days: r.days, from: r.from, slos: r.slos.map(toSlo) };
}

export async function setSloTarget(key: string, target: number): Promise<void> {
	await api(`/admin/slo/targets/${encodeURIComponent(key)}`, {
		method: 'PUT',
		body: JSON.stringify({ target })
	});
}

/** Put an objective back on its built-in threshold. */
export async function resetSloTarget(key: string): Promise<void> {
	await api(`/admin/slo/targets/${encodeURIComponent(key)}`, { method: 'DELETE' });
}
