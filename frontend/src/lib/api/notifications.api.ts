// Outbound notification channels (`/api/notifications/channels`, Admin throughout).
//
// Distinct from `messaging.api.ts`, which drives the *bidirectional* chat channels
// (Slack / Telegram / WhatsApp / Teams). This one is fire-and-forget: one URL, one
// direction, no identity, no conversation.
//
// The webhook URL is write-only: for Slack and Teams the URL *is* the credential,
// so the API returns `has_webhook_url` and a bare host instead. Omitting it on
// update keeps the stored value — there is nothing for the form to resubmit.

import { api, type ListResponse } from '$lib/api/client';

export const CHANNEL_KINDS = ['slack', 'teams', 'webhook'] as const;

export type NotificationChannel = {
	id: string;
	name: string;
	slug: string;
	kind: string;
	description: string | null;
	targetHost: string | null;
	hasWebhookUrl: boolean;
	headers: string | null;
	allowPrivateNetwork: boolean;
	status: string;
	lastCheckError: string | null;
	lastCheckedAt: string | null;
	enabled: boolean;
	updatedAt: string;
};

export interface ChannelPayload {
	name: string;
	kind: string;
	description: string;
	webhookUrl: string;
	headers: string;
	allowPrivateNetwork: boolean;
	enabled: boolean;
}

export type Delivery = {
	id: string;
	preview: string;
	success: boolean;
	statusCode: number | null;
	error: string | null;
	attempts: number;
	elapsedMs: number;
	workflowRunId: string | null;
	sentAt: string;
};

export type SendResult = {
	success: boolean;
	statusCode: number | null;
	error: string | null;
	attempts: number;
	elapsedMs: number;
};

interface ChannelShape {
	notification_channel_id: string;
	name: string;
	slug: string;
	kind: string;
	description: string | null;
	target_host: string | null;
	has_webhook_url: boolean;
	headers: string | null;
	allow_private_network: boolean;
	status: string;
	last_check_error: string | null;
	last_checked_at: string | null;
	enabled: boolean;
	updated_at: string;
}

interface DeliveryShape {
	notification_delivery_id: string;
	preview: string;
	success: boolean;
	status_code: number | null;
	error: string | null;
	attempts: number;
	elapsed_ms: number;
	workflow_run_id: string | null;
	sent_at: string;
}

function toChannel(c: ChannelShape): NotificationChannel {
	return {
		id: c.notification_channel_id,
		name: c.name,
		slug: c.slug,
		kind: c.kind,
		description: c.description,
		targetHost: c.target_host,
		hasWebhookUrl: c.has_webhook_url,
		headers: c.headers,
		allowPrivateNetwork: c.allow_private_network,
		status: c.status,
		lastCheckError: c.last_check_error,
		lastCheckedAt: c.last_checked_at,
		enabled: c.enabled,
		updatedAt: c.updated_at
	};
}

function toBody(p: ChannelPayload) {
	return {
		name: p.name,
		kind: p.kind,
		description: p.description || null,
		// Empty means "leave the stored URL alone".
		webhook_url: p.webhookUrl.trim() || null,
		headers: p.headers.trim() || null,
		allow_private_network: p.allowPrivateNetwork,
		enabled: p.enabled
	};
}

export async function listChannels(limit = 100, offset = 0): Promise<ListResponse<NotificationChannel>> {
	const res = await api<ListResponse<ChannelShape>>(
		`/notifications/channels?limit=${limit}&offset=${offset}`
	);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toChannel) };
}

export async function createChannel(p: ChannelPayload): Promise<NotificationChannel> {
	return toChannel(
		await api<ChannelShape>('/notifications/channels', { method: 'POST', body: JSON.stringify(toBody(p)) })
	);
}

export async function updateChannel(id: string, p: ChannelPayload): Promise<NotificationChannel> {
	return toChannel(
		await api<ChannelShape>(`/notifications/channels/${id}`, {
			method: 'PUT',
			body: JSON.stringify(toBody(p))
		})
	);
}

export async function deleteChannel(id: string): Promise<void> {
	await api<ChannelShape>(`/notifications/channels/${id}`, { method: 'DELETE' });
}

// Sends a real message — there is no dry run, because the only way to know a
// webhook works is for something to arrive at the far end.
export async function sendMessage(id: string, text: string): Promise<SendResult> {
	const r = await api<{
		success: boolean;
		status_code: number | null;
		error: string | null;
		attempts: number;
		elapsed_ms: number;
	}>(`/notifications/channels/${id}/send`, { method: 'POST', body: JSON.stringify({ text }) });
	return {
		success: r.success,
		statusCode: r.status_code,
		error: r.error,
		attempts: r.attempts,
		elapsedMs: r.elapsed_ms
	};
}

export async function checkChannel(
	id: string
): Promise<{ status: string; error: string | null; elapsedMs: number }> {
	const r = await api<{ status: string; error: string | null; elapsed_ms: number }>(
		`/notifications/channels/${id}/check`,
		{ method: 'POST' }
	);
	return { status: r.status, error: r.error, elapsedMs: r.elapsed_ms };
}

export async function listDeliveries(id: string, limit = 50): Promise<ListResponse<Delivery>> {
	const res = await api<ListResponse<DeliveryShape>>(
		`/notifications/channels/${id}/deliveries?limit=${limit}`
	);
	return {
		total: res.total,
		limit: res.limit,
		offset: res.offset,
		items: res.items.map((d) => ({
			id: d.notification_delivery_id,
			preview: d.preview,
			success: d.success,
			statusCode: d.status_code,
			error: d.error,
			attempts: d.attempts,
			elapsedMs: d.elapsed_ms,
			workflowRunId: d.workflow_run_id,
			sentAt: d.sent_at
		}))
	};
}
