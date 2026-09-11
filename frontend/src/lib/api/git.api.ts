// Git repositories + working-copy operations. Reads are Viewer; repository CRUD is
// Admin; git operations (pull/push/checkout/commit) are Operator. snake_case DTOs
// mapped to idiomatic types; `GitRepo` is a `type` alias for DataTable rows.

import { api, type ListResponse } from '$lib/api/client';

export type GitRepo = {
	id: string;
	name: string;
	url: string;
	defaultBranch: string;
	authCredentialId: string | null;
	description: string | null;
	localPath: string | null;
	lastFetchedAt: string | null;
	createdAt: string;
	updatedAt: string;
};

export interface RepoPayload {
	name: string;
	url: string;
	defaultBranch: string;
	description: string;
	authCredentialId: string;
}

export interface WriteFilePayload {
	path: string;
	content: string;
	commitMessage: string;
	branch?: string;
	push?: boolean;
}

export interface GitFile {
	path: string;
	type: string; // 'blob' | 'tree'
	size: number;
}

export interface FileList {
	ref: string;
	path: string;
	entries: GitFile[];
}

export interface FileContent {
	path: string;
	ref: string;
	content: string;
	size: number;
	isBinary: boolean;
}

export interface GitOpResult {
	ok: boolean;
	message: string;
	commitSha: string | null;
	branch: string | null;
}

export interface Branches {
	current: string;
	branches: string[];
}

export interface GitDiff {
	from: string;
	to: string;
	path: string | null;
	patch: string;
}

export interface CommitPayload {
	message: string;
	push: boolean;
	authorName?: string;
	authorEmail?: string;
}

interface RepoShape {
	git_repository_id: string;
	name: string;
	url: string;
	default_branch: string;
	auth_credential_id: string | null;
	description: string | null;
	local_path: string | null;
	last_fetched_at: string | null;
	created_at: string;
	updated_at: string;
}

interface OpShape {
	ok: boolean;
	message: string;
	commit_sha: string | null;
	branch: string | null;
}

function toRepo(r: RepoShape): GitRepo {
	return {
		id: r.git_repository_id,
		name: r.name,
		url: r.url,
		defaultBranch: r.default_branch,
		authCredentialId: r.auth_credential_id,
		description: r.description,
		localPath: r.local_path,
		lastFetchedAt: r.last_fetched_at,
		createdAt: r.created_at,
		updatedAt: r.updated_at
	};
}

function toOp(o: OpShape): GitOpResult {
	return { ok: o.ok, message: o.message, commitSha: o.commit_sha, branch: o.branch };
}

function repoBody(p: RepoPayload) {
	return {
		name: p.name,
		url: p.url,
		default_branch: p.defaultBranch || 'main',
		description: p.description,
		auth_credential_id: p.authCredentialId || null
	};
}

const base = (id: string) => `/git/repositories/${id}`;

export async function listRepos(limit = 100, offset = 0): Promise<ListResponse<GitRepo>> {
	const res = await api<ListResponse<RepoShape>>(`/git/repositories?limit=${limit}&offset=${offset}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toRepo) };
}

export async function getRepo(id: string): Promise<GitRepo> {
	return toRepo(await api<RepoShape>(base(id)));
}

export async function createRepo(p: RepoPayload): Promise<GitRepo> {
	return toRepo(await api<RepoShape>('/git/repositories', { method: 'POST', body: JSON.stringify(repoBody(p)) }));
}

export async function updateRepo(id: string, p: RepoPayload): Promise<GitRepo> {
	return toRepo(await api<RepoShape>(base(id), { method: 'PUT', body: JSON.stringify(repoBody(p)) }));
}

export async function deleteRepo(id: string): Promise<void> {
	await api<RepoShape>(base(id), { method: 'DELETE' });
}

export async function getBranches(id: string): Promise<Branches> {
	const r = await api<{ current: string; branches: string[] }>(`${base(id)}/branches`);
	return { current: r.current, branches: r.branches ?? [] };
}

export async function pull(id: string, branch = ''): Promise<GitOpResult> {
	const q = branch ? `?branch=${encodeURIComponent(branch)}` : '';
	return toOp(await api<OpShape>(`${base(id)}/pull${q}`, { method: 'POST' }));
}

export async function push(id: string, branch = ''): Promise<GitOpResult> {
	const q = branch ? `?branch=${encodeURIComponent(branch)}` : '';
	return toOp(await api<OpShape>(`${base(id)}/push${q}`, { method: 'POST' }));
}

export async function checkout(id: string, branch: string): Promise<GitOpResult> {
	return toOp(await api<OpShape>(`${base(id)}/checkout?branch=${encodeURIComponent(branch)}`, { method: 'POST' }));
}

export async function listFiles(id: string, path = '', ref = ''): Promise<FileList> {
	const qs = new URLSearchParams();
	if (path) qs.set('path', path);
	if (ref) qs.set('ref', ref);
	const q = qs.toString();
	const r = await api<{ ref: string; path: string; entries: GitFile[] }>(
		`${base(id)}/files${q ? `?${q}` : ''}`
	);
	return { ref: r.ref, path: r.path, entries: r.entries ?? [] };
}

export async function readFile(id: string, path: string, ref = ''): Promise<FileContent> {
	const qs = new URLSearchParams({ path });
	if (ref) qs.set('ref', ref);
	const r = await api<{ path: string; ref: string; content: string; size: number; is_binary: boolean }>(
		`${base(id)}/file?${qs.toString()}`
	);
	return { path: r.path, ref: r.ref, content: r.content, size: r.size, isBinary: r.is_binary };
}

export async function writeFile(id: string, p: WriteFilePayload): Promise<GitOpResult> {
	return toOp(
		await api<OpShape>(`${base(id)}/file`, {
			method: 'PUT',
			body: JSON.stringify({
				path: p.path,
				content: p.content,
				commit_message: p.commitMessage,
				branch: p.branch || null,
				push: p.push ?? false
			})
		})
	);
}

export async function getDiff(id: string, from = '', to = '', path = ''): Promise<GitDiff> {
	const qs = new URLSearchParams();
	if (from) qs.set('from', from);
	if (to) qs.set('to', to);
	if (path) qs.set('path', path);
	const q = qs.toString();
	const r = await api<{ from: string; to: string; path: string | null; patch: string }>(
		`${base(id)}/diff${q ? `?${q}` : ''}`
	);
	return { from: r.from, to: r.to, path: r.path, patch: r.patch };
}

export async function commit(id: string, p: CommitPayload): Promise<GitOpResult> {
	return toOp(
		await api<OpShape>(`${base(id)}/commit`, {
			method: 'POST',
			body: JSON.stringify({
				commit_message: p.message,
				push: p.push,
				author_name: p.authorName,
				author_email: p.authorEmail
			})
		})
	);
}

// ── Webhooks ────────────────────────────────────────────────────────────────
// Inbound receivers on a repository: a verified push pulls the working copy and
// optionally runs a workflow. Reads are Viewer; everything that mutates is Admin,
// like repository registration itself.

export type GitWebhookProvider = 'github' | 'gitlab' | 'generic';

export interface GitWebhook {
	id: string;
	name: string;
	provider: GitWebhookProvider;
	// Path only. The origin is this page's, because the server has no way to know
	// which hostname it is reachable on from GitHub.
	ingestPath: string;
	signatureHeader: string;
	hasSecret: boolean;
	allowUnsigned: boolean;
	onPushWorkflowId: string | null;
	onPushWorkflowName: string | null;
	onPushBranches: string[];
	autoPull: boolean;
	enabled: boolean;
	lastDeliveryAt: string | null;
	lastDeliveryStatus: string | null;
	deliveryCount: number;
	// Returned exactly once, by create and rotate-secret. Nothing reads it back.
	secret: string | null;
}

export interface WebhookPayload {
	name: string;
	provider: GitWebhookProvider;
	onPushWorkflowId: string | null;
	onPushBranches: string[];
	autoPull: boolean;
	enabled: boolean;
	allowUnsigned: boolean;
}

export interface GitWebhookDelivery {
	id: string;
	at: string;
	status: string;
	event: string | null;
	branch: string | null;
	commitSha: string | null;
	jobId: string | null;
	error: string | null;
}

export interface GitWebhookDryRun {
	wouldDispatch: boolean;
	reason: string;
	branch: string | null;
	workflowId: string | null;
}

interface WebhookShape {
	git_webhook_id: string;
	name: string;
	provider: GitWebhookProvider;
	ingest_path: string;
	signature_header: string;
	has_secret: boolean;
	allow_unsigned: boolean;
	on_push_workflow_id: string | null;
	on_push_workflow_name: string | null;
	on_push_branches: string[];
	auto_pull: boolean;
	enabled: boolean;
	last_delivery_at: string | null;
	last_delivery_status: string | null;
	delivery_count: number;
	secret?: string | null;
}

function toWebhook(w: WebhookShape): GitWebhook {
	return {
		id: w.git_webhook_id,
		name: w.name,
		provider: w.provider,
		ingestPath: w.ingest_path,
		signatureHeader: w.signature_header,
		hasSecret: w.has_secret,
		allowUnsigned: w.allow_unsigned,
		onPushWorkflowId: w.on_push_workflow_id,
		onPushWorkflowName: w.on_push_workflow_name,
		onPushBranches: w.on_push_branches ?? [],
		autoPull: w.auto_pull,
		enabled: w.enabled,
		lastDeliveryAt: w.last_delivery_at,
		lastDeliveryStatus: w.last_delivery_status,
		deliveryCount: w.delivery_count,
		secret: w.secret ?? null
	};
}

function webhookBody(p: Partial<WebhookPayload>) {
	return {
		name: p.name,
		provider: p.provider,
		on_push_workflow_id: p.onPushWorkflowId || null,
		on_push_branches: p.onPushBranches ?? [],
		auto_pull: p.autoPull,
		enabled: p.enabled,
		allow_unsigned: p.allowUnsigned
	};
}

const hooks = (repoId: string) => `${base(repoId)}/webhooks`;

export async function listWebhooks(repoId: string): Promise<GitWebhook[]> {
	const res = await api<ListResponse<WebhookShape>>(hooks(repoId));
	return res.items.map(toWebhook);
}

export async function createWebhook(repoId: string, p: WebhookPayload): Promise<GitWebhook> {
	return toWebhook(
		await api<WebhookShape>(hooks(repoId), { method: 'POST', body: JSON.stringify(webhookBody(p)) })
	);
}

export async function updateWebhook(
	repoId: string,
	id: string,
	p: Partial<WebhookPayload>
): Promise<GitWebhook> {
	// The provider is not editable server-side — it decides which header the sender
	// signs with — so it is not sent here either.
	const { provider: _provider, ...body } = webhookBody(p);
	return toWebhook(
		await api<WebhookShape>(`${hooks(repoId)}/${id}`, {
			method: 'PUT',
			body: JSON.stringify({ ...body, clear_on_push_workflow: p.onPushWorkflowId === null })
		})
	);
}

export async function deleteWebhook(repoId: string, id: string): Promise<void> {
	await api<WebhookShape>(`${hooks(repoId)}/${id}`, { method: 'DELETE' });
}

export async function rotateWebhookSecret(repoId: string, id: string): Promise<GitWebhook> {
	return toWebhook(await api<WebhookShape>(`${hooks(repoId)}/${id}/rotate-secret`, { method: 'POST' }));
}

export async function listDeliveries(repoId: string, id: string, limit = 50): Promise<GitWebhookDelivery[]> {
	const res = await api<
		ListResponse<{
			git_webhook_delivery_id: string;
			at: string;
			status: string;
			event: string | null;
			branch: string | null;
			commit_sha: string | null;
			job_id: string | null;
			error: string | null;
		}>
	>(`${hooks(repoId)}/${id}/deliveries?limit=${limit}`);
	return res.items.map((d) => ({
		id: d.git_webhook_delivery_id,
		at: d.at,
		status: d.status,
		event: d.event,
		branch: d.branch,
		commitSha: d.commit_sha,
		jobId: d.job_id,
		error: d.error
	}));
}

export async function dryRunWebhook(
	repoId: string,
	id: string,
	branch: string
): Promise<GitWebhookDryRun> {
	const q = branch ? `?branch=${encodeURIComponent(branch)}` : '';
	const r = await api<{
		would_dispatch: boolean;
		reason: string;
		branch: string | null;
		workflow_id: string | null;
	}>(`${hooks(repoId)}/${id}/dry-run${q}`);
	return {
		wouldDispatch: r.would_dispatch,
		reason: r.reason,
		branch: r.branch,
		workflowId: r.workflow_id
	};
}
