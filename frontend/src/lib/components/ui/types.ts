// Shared design-system types.

export type Tone = 'neutral' | 'primary' | 'success' | 'warning' | 'error';
export type Size = 'sm' | 'md' | 'lg';

// DataTable column descriptor.
export interface Column {
	key: string;
	header: string;
	align?: 'left' | 'right' | 'center';
	width?: string;
	// Opt a column out of sorting (action columns, rendered-only cells). Columns
	// are sortable by default: a table you can't order is unusable past ~20 rows.
	sortable?: boolean;
	// Pull the sort value when the raw row[key] isn't what should be compared
	// (derived cells, nested fields, dates stored as strings).
	sortValue?: (row: never) => string | number | boolean | null | undefined;
}

// Row spacing. `compact` is for scanning long lists; `comfortable` is the default.
export type Density = 'comfortable' | 'compact';

// StackedBarChart. One bucket per column; `counts` is keyed by series key, and a key
// absent from a bucket counts as zero rather than as missing data.
export interface BarBucket {
	label: string;
	title?: string;
	counts: Record<string, number>;
}

export interface BarSeries {
	key: string;
	label: string;
	/** Tailwind background class. Drawn bottom to top in array order. */
	class: string;
}
