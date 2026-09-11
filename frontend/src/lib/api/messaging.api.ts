// Bidirectional messaging channels (`/api/messaging`, Admin throughout) — Slack,
// Telegram, WhatsApp and Teams talking *to* the agent and back.
//
// Distinct from `notifications.api.ts`, which is fire-and-forget outbound: one URL,
// one direction, no identity. Here a channel row is a bot credential plus the
// permission ceiling for everyone who speaks through it, and every inbound message
// is attributed to a Nashira user via an identity link.
//
// SECRET SEMANTICS. The three credentials are write-only with three states:
//   key absent → leave unchanged      "" → clear      value → rotate
// The API never returns them (only `has_*` booleans), so "absent means clear" would
// silently unconfigure a channel on any unrelated edit. `ChannelPayload` models this
// with `undefined` for "don't touch", and `toBody()` drops those keys entirely.

import { api, type ListResponse } from '$lib/api/client';

export const MESSAGING_PROVIDERS = ['telegram', 'slack', 'whatsapp', 'teams'] as const;

export type MessagingProvider = (typeof MESSAGING_PROVIDERS)[number];

// A type alias, not an interface: DataTable's row generic requires an implicit
// index signature, which only object-literal type aliases get.
export type MessagingChannel = {
	id: string;
	provider: string;
	name: string;
	slug: string;
	externalConfig: Record<string, string>;
	maxRole: string | null;
	requireLinkedUser: boolean;
	allowedExternalIds: string[];
	allowUnsigned: boolean;
	enabled: boolean;
	hasBotToken: boolean;
	hasSigningSecret: boolean;
	hasAppToken: boolean;
	/** Empty when Messaging:PublicBaseUrl is unset — half a URL is worse than none. */
	webhookUrl: string;
	lastDeliveryAt: string | null;
	lastDeliveryStatus: string | null;
	createdAt: string;
	updatedAt: string;
};

export interface ChannelPayload {
	provider: string;
	name: string;
	/** undefined = leave unchanged · '' = clear · value = rotate. */
	botToken?: string;
	signingSecret?: string;
	appToken?: string;
	externalConfig: Record<string, string>;
	maxRole: string | null;
	requireLinkedUser: boolean;
	allowedExternalIds: string[];
	allowUnsigned: boolean;
	enabled: boolean;
}

export type InboundEvent = {
	id: string;
	providerEventId: string;
	externalThreadId: string | null;
	conversationId: string | null;
	status: string;
	event: string | null;
	error: string | null;
	at: string;
};

export type MessagingDelivery = {
	id: string;
	conversationId: string | null;
	externalThreadId: string | null;
	status: string;
	attempt: number;
	error: string | null;
	at: string;
};

export interface ChannelActivity {
	inbound: InboundEvent[];
	deliveries: MessagingDelivery[];
}

export type MessagingIdentityLink = {
	id: string;
	channelId: string;
	externalWorkspaceId: string;
	externalUserId: string;
	linkedUserId: string;
	linkedUsername: string | null;
	displayName: string | null;
	createdAt: string;
};

export interface LinkPreview {
	provider: string;
	channelName: string;
	externalUserId: string;
	expiresAt: string;
}

export interface LinkConfirm {
	linked: boolean;
	linkId: string;
}

// ─── wire shapes (snake_case) ─────────────────────────────────────────────

interface ChannelShape {
	messaging_channel_id: string;
	provider: string;
	name: string;
	slug: string;
	external_config: Record<string, string>;
	max_role: string | null;
	require_linked_user: boolean;
	allowed_external_ids: string[];
	allow_unsigned: boolean;
	enabled: boolean;
	has_bot_token: boolean;
	has_signing_secret: boolean;
	has_app_token: boolean;
	webhook_url: string;
	last_delivery_at: string | null;
	last_delivery_status: string | null;
	created_at: string;
	updated_at: string;
}

interface InboundEventShape {
	messaging_inbound_event_id: string;
	provider_event_id: string;
	external_thread_id: string | null;
	conversation_id: string | null;
	status: string;
	event: string | null;
	error: string | null;
	at: string;
}

interface DeliveryShape {
	messaging_delivery_id: string;
	conversation_id: string | null;
	external_thread_id: string | null;
	status: string;
	attempt: number;
	error: string | null;
	at: string;
}

interface ActivityShape {
	inbound: InboundEventShape[];
	deliveries: DeliveryShape[];
}

interface IdentityLinkShape {
	messaging_identity_link_id: string;
	messaging_channel_id: string;
	external_workspace_id: string;
	external_user_id: string;
	linked_user_id: string;
	linked_username: string | null;
	display_name: string | null;
	created_at: string;
}

interface LinkPreviewShape {
	provider: string;
	channel_name: string;
	external_user_id: string;
	expires_at: string;
}

interface LinkConfirmShape {
	linked: boolean;
	messaging_identity_link_id: string;
}

// ─── mappers ──────────────────────────────────────────────────────────────

function toChannel(c: ChannelShape): MessagingChannel {
	return {
		id: c.messaging_channel_id,
		provider: c.provider,
		name: c.name,
		slug: c.slug,
		externalConfig: c.external_config ?? {},
		maxRole: c.max_role,
		requireLinkedUser: c.require_linked_user,
		allowedExternalIds: c.allowed_external_ids ?? [],
		allowUnsigned: c.allow_unsigned,
		enabled: c.enabled,
		hasBotToken: c.has_bot_token,
		hasSigningSecret: c.has_signing_secret,
		hasAppToken: c.has_app_token,
		webhookUrl: c.webhook_url ?? '',
		lastDeliveryAt: c.last_delivery_at,
		lastDeliveryStatus: c.last_delivery_status,
		createdAt: c.created_at,
		updatedAt: c.updated_at
	};
}

function toInbound(e: InboundEventShape): InboundEvent {
	return {
		id: e.messaging_inbound_event_id,
		providerEventId: e.provider_event_id,
		externalThreadId: e.external_thread_id,
		conversationId: e.conversation_id,
		status: e.status,
		event: e.event,
		error: e.error,
		at: e.at
	};
}

function toDelivery(d: DeliveryShape): MessagingDelivery {
	return {
		id: d.messaging_delivery_id,
		conversationId: d.conversation_id,
		externalThreadId: d.external_thread_id,
		status: d.status,
		attempt: d.attempt,
		error: d.error,
		at: d.at
	};
}

function toLink(l: IdentityLinkShape): MessagingIdentityLink {
	return {
		id: l.messaging_identity_link_id,
		channelId: l.messaging_channel_id,
		externalWorkspaceId: l.external_workspace_id,
		externalUserId: l.external_user_id,
		linkedUserId: l.linked_user_id,
		linkedUsername: l.linked_username,
		displayName: l.display_name,
		createdAt: l.created_at
	};
}

function toBody(p: ChannelPayload): Record<string, unknown> {
	const body: Record<string, unknown> = {
		provider: p.provider,
		name: p.name,
		external_config: p.externalConfig,
		max_role: p.maxRole,
		require_linked_user: p.requireLinkedUser,
		allowed_external_ids: p.allowedExternalIds,
		allow_unsigned: p.allowUnsigned,
		enabled: p.enabled
	};
	// Only the secrets the caller actually decided about. An omitted key keeps the
	// stored credential; sending '' would clear it.
	if (p.botToken !== undefined) body.bot_token = p.botToken;
	if (p.signingSecret !== undefined) body.signing_secret = p.signingSecret;
	if (p.appToken !== undefined) body.app_token = p.appToken;
	return body;
}

// ─── channels ─────────────────────────────────────────────────────────────

export async function listChannels(limit = 100, offset = 0): Promise<ListResponse<MessagingChannel>> {
	const res = await api<ListResponse<ChannelShape>>(
		`/messaging/channels?limit=${limit}&offset=${offset}`
	);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toChannel) };
}

export async function getChannel(id: string): Promise<MessagingChannel> {
	return toChannel(await api<ChannelShape>(`/messaging/channels/${id}`));
}

export async function createChannel(p: ChannelPayload): Promise<MessagingChannel> {
	return toChannel(
		await api<ChannelShape>('/messaging/channels', { method: 'POST', body: JSON.stringify(toBody(p)) })
	);
}

export async function updateChannel(id: string, p: ChannelPayload): Promise<MessagingChannel> {
	return toChannel(
		await api<ChannelShape>(`/messaging/channels/${id}`, {
			method: 'PUT',
			body: JSON.stringify(toBody(p))
		})
	);
}

export async function deleteChannel(id: string): Promise<void> {
	await api<ChannelShape>(`/messaging/channels/${id}`, { method: 'DELETE' });
}

// The two halves of "why did the bot not answer?" — what arrived, and what left.
export async function getActivity(id: string, limit = 50): Promise<ChannelActivity> {
	const res = await api<ActivityShape>(`/messaging/channels/${id}/activity?limit=${limit}`);
	return {
		inbound: (res.inbound ?? []).map(toInbound),
		deliveries: (res.deliveries ?? []).map(toDelivery)
	};
}

export async function listLinks(id: string): Promise<MessagingIdentityLink[]> {
	const res = await api<ListResponse<IdentityLinkShape>>(`/messaging/channels/${id}/links`);
	return res.items.map(toLink);
}

// Does not delete conversation history — it stops the external identity acting as
// that user from the next message on.
export async function revokeLink(id: string, linkId: string): Promise<void> {
	await api<void>(`/messaging/channels/${id}/links/${linkId}`, { method: 'DELETE' });
}

// ─── self-service linking (any signed-in role) ────────────────────────────

export async function previewLink(token: string): Promise<LinkPreview> {
	const r = await api<LinkPreviewShape>(`/messaging/link/${encodeURIComponent(token)}`);
	return {
		provider: r.provider,
		channelName: r.channel_name,
		externalUserId: r.external_user_id,
		expiresAt: r.expires_at
	};
}

export async function confirmLink(token: string): Promise<LinkConfirm> {
	const r = await api<LinkConfirmShape>(`/messaging/link/${encodeURIComponent(token)}/confirm`, {
		method: 'POST'
	});
	return { linked: r.linked, linkId: r.messaging_identity_link_id };
}
