// Chat conversation history/CRUD — GET list, GET detail, DELETE. A user only ever
// sees their own threads (enforced server-side). Wire DTOs are snake_case
// (`[JsonPropertyName]`), mapped to idiomatic camelCase here, matching the auth
// slice's *Shape pattern.

import { api, type ListResponse } from '$lib/api/client';

export interface ConversationSummary {
	id: string;
	title: string | null;
	status: string;
	tokensIn: number;
	tokensOut: number;
	createdAt: string;
	updatedAt: string;
	// Which provider/model last answered this thread. Null on conversations
	// started before the chat could choose one; those fall back to the default.
	aiProviderId: string | null;
	model: string | null;
}

// Persisted history is text-only turns (role + content); live tool calls are not
// stored, so a reloaded conversation shows just the user/assistant messages.
// `attachments` holds filenames only (for the paperclip chips) — content is never
// persisted, and rows written before the field existed simply omit it.
export interface StoredMessage {
	role: 'user' | 'assistant';
	content: string;
	attachments?: string[];
}

export interface ConversationDetail extends ConversationSummary {
	messages: StoredMessage[];
}

interface ConversationSummaryShape {
	conversation_id: string;
	title: string | null;
	status: string;
	tokens_in: number;
	tokens_out: number;
	created_at: string;
	updated_at: string;
	ai_provider_id: string | null;
	model: string | null;
}

interface ConversationDetailShape extends ConversationSummaryShape {
	messages: unknown;
}

function toSummary(s: ConversationSummaryShape): ConversationSummary {
	return {
		id: s.conversation_id,
		title: s.title,
		status: s.status,
		tokensIn: s.tokens_in,
		tokensOut: s.tokens_out,
		createdAt: s.created_at,
		updatedAt: s.updated_at,
		aiProviderId: s.ai_provider_id ?? null,
		model: s.model ?? null
	};
}

function toMessages(raw: unknown): StoredMessage[] {
	if (!Array.isArray(raw)) return [];
	return raw
		.filter(
			(m): m is StoredMessage =>
				!!m &&
				typeof m === 'object' &&
				(('role' in m && (m.role === 'user' || m.role === 'assistant')) as boolean) &&
				'content' in m &&
				typeof (m as { content: unknown }).content === 'string'
		)
		.map((m) => ({
			role: m.role,
			content: m.content,
			attachments: Array.isArray(m.attachments)
				? m.attachments.filter((a): a is string => typeof a === 'string')
				: undefined
		}));
}

export async function listConversations(limit = 50, offset = 0): Promise<ListResponse<ConversationSummary>> {
	const res = await api<ListResponse<ConversationSummaryShape>>(
		`/ai/conversations?limit=${limit}&offset=${offset}`
	);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toSummary) };
}

export async function getConversation(id: string): Promise<ConversationDetail> {
	const s = await api<ConversationDetailShape>(`/ai/conversations/${id}`);
	return { ...toSummary(s), messages: toMessages(s.messages) };
}

export async function deleteConversation(id: string): Promise<void> {
	await api<void>(`/ai/conversations/${id}`, { method: 'DELETE' });
}
