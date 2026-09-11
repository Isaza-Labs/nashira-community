// Agent sessions (`/api/sessions`, Admin): every conversation in the installation,
// with the turn telemetry recorded alongside it.
//
// Deliberately not the same thing as `/ai/conversations`, which only ever returns the
// caller's own history. This is the oversight view — what has been asked of the agent
// across the organisation, and what the agent did about it. The audit trail answers
// "what changed"; this answers "what happened", including the turns that changed
// nothing and the tool calls that failed.

import { api, type ListResponse } from '$lib/api/client';

export type SessionSummary = {
	id: string;
	userId: string | null;
	username: string | null;
	title: string | null;
	status: string;
	messageCount: number;
	turnCount: number;
	toolCallCount: number;
	failedTurnCount: number;
	tokensIn: number;
	tokensOut: number;
	createdAt: string;
	updatedAt: string;
};

export type SessionMessage = { role: string; content: string };

// One recorded tool call. Secret-bearing argument values are replaced with a marker
// server-side, before the row is written — never here.
export type SessionToolCall = {
	name: string;
	ok: boolean;
	elapsed_ms: number;
	arguments?: unknown;
	result?: unknown;
};

export type SessionTurn = {
	id: string;
	model: string;
	userMessage: string;
	assistantText: string | null;
	toolCalls: SessionToolCall[];
	toolCallCount: number;
	tokensIn: number;
	tokensOut: number;
	iterations: number;
	status: string;
	error: string | null;
	startedAt: string;
	elapsedMs: number;
};

export type SessionDetail = SessionSummary & {
	messages: SessionMessage[];
	turns: SessionTurn[];
};

interface SummaryShape {
	conversation_id: string;
	user_id: string | null;
	username: string | null;
	title: string | null;
	status: string;
	message_count: number;
	turn_count: number;
	tool_call_count: number;
	failed_turn_count: number;
	tokens_in: number;
	tokens_out: number;
	created_at: string;
	updated_at: string;
}

interface TurnShape {
	agent_turn_id: string;
	model: string;
	user_message: string;
	assistant_text: string | null;
	tool_calls: unknown;
	tool_call_count: number;
	tokens_in: number;
	tokens_out: number;
	iterations: number;
	status: string;
	error: string | null;
	started_at: string;
	elapsed_ms: number;
}

interface DetailShape extends SummaryShape {
	messages: SessionMessage[];
	turns: TurnShape[];
}

function toSummary(s: SummaryShape): SessionSummary {
	return {
		id: s.conversation_id,
		userId: s.user_id,
		username: s.username,
		title: s.title,
		status: s.status,
		messageCount: s.message_count,
		turnCount: s.turn_count,
		toolCallCount: s.tool_call_count,
		failedTurnCount: s.failed_turn_count,
		tokensIn: s.tokens_in,
		tokensOut: s.tokens_out,
		createdAt: s.created_at,
		updatedAt: s.updated_at
	};
}

function toTurn(t: TurnShape): SessionTurn {
	return {
		id: t.agent_turn_id,
		model: t.model,
		userMessage: t.user_message,
		assistantText: t.assistant_text,
		// Stored as JSON and read back as-is; a malformed blob must not blank the page.
		toolCalls: Array.isArray(t.tool_calls) ? (t.tool_calls as SessionToolCall[]) : [],
		toolCallCount: t.tool_call_count,
		tokensIn: t.tokens_in,
		tokensOut: t.tokens_out,
		iterations: t.iterations,
		status: t.status,
		error: t.error,
		startedAt: t.started_at,
		elapsedMs: t.elapsed_ms
	};
}

export async function listSessions(limit = 50, offset = 0): Promise<ListResponse<SessionSummary>> {
	const res = await api<ListResponse<SummaryShape>>(`/sessions?limit=${limit}&offset=${offset}`);
	return { items: res.items.map(toSummary), total: res.total, limit: res.limit, offset: res.offset };
}

export async function getSession(id: string): Promise<SessionDetail> {
	const s = await api<DetailShape>(`/sessions/${id}`);
	return {
		...toSummary(s),
		messages: Array.isArray(s.messages) ? s.messages : [],
		turns: (s.turns ?? []).map(toTurn)
	};
}
