// Time formatting for an operations console: what matters when scanning a table
// is freshness ("4m ago"), not a full locale timestamp. The absolute value stays
// available as a title/tooltip so nothing is lost.

const UNITS: [limitSeconds: number, perUnit: number, unit: Intl.RelativeTimeFormatUnit][] = [
	[60, 1, 'second'],
	[3600, 60, 'minute'],
	[86400, 3600, 'hour'],
	[604800, 86400, 'day'],
	[2629800, 604800, 'week'],
	[31557600, 2629800, 'month'],
	[Infinity, 31557600, 'year']
];

const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

function parse(iso: string | null | undefined): Date | null {
	if (!iso) return null;
	const d = new Date(iso);
	return Number.isNaN(d.getTime()) ? null : d;
}

// "just now" / "4 minutes ago" / "in 2 days". Falls back to the raw input when
// it isn't a parseable date, so a bad value is visible rather than silently blank.
export function timeAgo(iso: string | null | undefined, now: Date = new Date()): string {
	const d = parse(iso);
	if (!d) return iso ?? '—';

	const diffSeconds = (d.getTime() - now.getTime()) / 1000;
	const abs = Math.abs(diffSeconds);
	if (abs < 30) return 'just now';

	for (const [limit, perUnit, unit] of UNITS) {
		if (abs < limit) return rtf.format(Math.round(diffSeconds / perUnit), unit);
	}
	return d.toLocaleString();
}

// Full timestamp for tooltips and detail views.
export function absolute(iso: string | null | undefined): string {
	const d = parse(iso);
	return d ? d.toLocaleString() : (iso ?? '—');
}

// True when the timestamp is within `seconds` of now — drives "live" affordances
// (a fresh-sync dot, a highlighted row) without every caller redoing the math.
export function isFresh(iso: string | null | undefined, seconds = 300): boolean {
	const d = parse(iso);
	return d ? Math.abs(Date.now() - d.getTime()) / 1000 < seconds : false;
}
