// nashira design system — single import surface for feature pages.
// Usage: import { Button, Card, toast, confirm } from '$lib/components/ui';

export { default as Alert } from './Alert.svelte';
export { default as Badge } from './Badge.svelte';
export { default as Button } from './Button.svelte';
export { default as Card } from './Card.svelte';
export { default as Checkbox } from './Checkbox.svelte';
export { default as CodeEditor, type CodeLanguage } from './CodeEditor.svelte';
export { default as DataTable } from './DataTable.svelte';
export { default as EmptyState } from './EmptyState.svelte';
export { default as ErrorState } from './ErrorState.svelte';
export { default as FieldHint } from './FieldHint.svelte';
export { default as IconButton } from './IconButton.svelte';
export { default as Input } from './Input.svelte';
export { default as Kbd } from './Kbd.svelte';
export { default as KeyValueRows } from './KeyValueRows.svelte';
export { default as Logo } from './Logo.svelte';
export { default as Mermaid } from './Mermaid.svelte';
export { default as Modal } from './Modal.svelte';
export { default as ModeToggle } from './ModeToggle.svelte';
export { default as PageHeader } from './PageHeader.svelte';
export { default as Pagination } from './Pagination.svelte';
export { default as SearchInput } from './SearchInput.svelte';
export { default as Select } from './Select.svelte';
export { default as Skeleton } from './Skeleton.svelte';
export { default as SortableTh } from './SortableTh.svelte';
export { default as Spinner } from './Spinner.svelte';
export { default as TableSkeleton } from './TableSkeleton.svelte';
export { default as StatCard } from './StatCard.svelte';
export { default as StackedBarChart } from './StackedBarChart.svelte';
export { default as StatusBadge } from './StatusBadge.svelte';
export { default as Tabs } from './Tabs.svelte';
export { default as Textarea } from './Textarea.svelte';
export { default as Toolbar } from './Toolbar.svelte';

// App-wide singletons (mount ConfirmHost + Toaster once in the root layout).
export { default as ConfirmHost } from './ConfirmHost.svelte';
export { confirm, confirmStore, type ConfirmOptions } from './confirm.svelte';
export { default as Toaster } from './Toaster.svelte';
export { toast, type Toast, type ToastInput, type ToastTone } from './toast.svelte';

// Shared design-system types.
export type { Tone, Size, Column, Density, BarBucket, BarSeries } from './types';
