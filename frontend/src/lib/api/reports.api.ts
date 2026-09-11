// Report artifacts (`/api/reports`). Viewer reads; Operator writes.

import { api, apiFile, type ListResponse } from '$lib/api/client';
import { saveBlob } from '$lib/utils/download';

export type Report = {
	id: string;
	title: string;
	description: string | null;
	contentType: string;
	fileName: string;
	sizeBytes: number;
	workflowRunId: string | null;
	expiresAt: string | null;
	createdAt: string;
};

export interface ReportPayload {
	title: string;
	description: string;
	content: string;
	contentType: string;
	fileName: string;
	retainDays: number | null;
}

interface ReportShape {
	report_artifact_id: string;
	title: string;
	description: string | null;
	content_type: string;
	file_name: string;
	size_bytes: number;
	workflow_run_id: string | null;
	expires_at: string | null;
	created_at: string;
}

function toReport(r: ReportShape): Report {
	return {
		id: r.report_artifact_id,
		title: r.title,
		description: r.description,
		contentType: r.content_type,
		fileName: r.file_name,
		sizeBytes: r.size_bytes,
		workflowRunId: r.workflow_run_id,
		expiresAt: r.expires_at,
		createdAt: r.created_at
	};
}

export async function listReports(includeExpired = false, limit = 100): Promise<ListResponse<Report>> {
	const res = await api<ListResponse<ReportShape>>(
		`/reports?includeExpired=${includeExpired}&limit=${limit}`
	);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toReport) };
}

export async function createReport(p: ReportPayload): Promise<Report> {
	return toReport(
		await api<ReportShape>('/reports', {
			method: 'POST',
			body: JSON.stringify({
				title: p.title,
				description: p.description || null,
				content: p.content,
				content_type: p.contentType || null,
				file_name: p.fileName || null,
				retain_days: p.retainDays
			})
		})
	);
}

export async function deleteReport(id: string): Promise<void> {
	await api<unknown>(`/reports/${id}`, { method: 'DELETE' });
}

// The fetch goes through the API client so it carries the auth header — a plain
// <a download> against the endpoint would arrive unauthenticated.
export async function downloadReport(r: Report): Promise<void> {
	await downloadReportById(r.id, r.fileName);
}

// Same download, reached by id alone. The chat gets here from a link the agent
// wrote (see $lib/utils/markdown), so it has no Report object to take the name
// from — Content-Disposition supplies it instead.
export async function downloadReportById(id: string, fileName?: string): Promise<void> {
	const res = await apiFile(`/reports/${id}/download`);
	saveBlob(res.blob, fileName || res.fileName || `report-${id}`);
}
