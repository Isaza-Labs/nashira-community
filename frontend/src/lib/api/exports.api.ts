// Export artifacts: list metadata + authenticated binary download. Creation
// happens through the export_table agent tool, not here. The download endpoint
// requires the bearer token, so it's fetched as a blob (not a bare <a href>, which
// would navigate without the Authorization header and 401) and saved via a
// temporary object URL. The chat's download buttons come through here too.

import { api, apiFile, type ListResponse } from '$lib/api/client';
import { saveBlob } from '$lib/utils/download';

export interface ExportArtifact {
	id: string;
	fileName: string;
	contentType: string;
	sizeBytes: number;
	createdAt: string;
}

interface ExportShape {
	export_artifact_id: string;
	file_name: string;
	content_type: string;
	size_bytes: number;
	created_at: string;
}

function toArtifact(e: ExportShape): ExportArtifact {
	return {
		id: e.export_artifact_id,
		fileName: e.file_name,
		contentType: e.content_type,
		sizeBytes: e.size_bytes,
		createdAt: e.created_at
	};
}

export async function listExports(limit = 100, offset = 0): Promise<ListResponse<ExportArtifact>> {
	const res = await api<ListResponse<ExportShape>>(`/export?limit=${limit}&offset=${offset}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toArtifact) };
}

export async function downloadExport(id: string, fileName?: string): Promise<void> {
	const res = await apiFile(`/export/${id}/download`);
	saveBlob(res.blob, fileName || res.fileName || `export-${id}`);
}
