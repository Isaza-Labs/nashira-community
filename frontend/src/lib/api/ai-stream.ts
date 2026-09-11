// Streaming client for POST /api/ai/chat. The backend emits Server-Sent Events
// (`data: {json}\n\n` per frame), each frame carrying a `type` the caller
// switches on. fetch + ReadableStream is used (not EventSource) because the turn
// is a POST with a body and needs the bearer header.
//
// Event shapes mirror the server's SseAgentEventSink exactly (snake_case):
//   { type: 'conversation', conversation_id: string, is_new: boolean }
//   { type: 'model',        ai_provider_id: string, provider: string, model: string }
//   { type: 'token',        delta: string }
//   { type: 'tool_start',   name: string, args: unknown }
//   { type: 'tool_result',  name: string, success: boolean, result: unknown }
//   { type: 'done',         tokens_in: number, tokens_out: number, iterations: number }
//   { type: 'error',        message: string, code: string | null }

import { authStore } from '$lib/stores/auth.svelte';
import { tryRefresh } from '$lib/api/client';

export type ChatStreamEvent =
	| { type: 'conversation'; conversation_id: string; is_new: boolean }
	// Which provider/model is answering. Sent on every turn, including turns that
	// named none: the server resolves the conversation's stored choice or the
	// tenant default, and this is how the selector learns which it got.
	| { type: 'model'; ai_provider_id: string; provider: string; model: string }
	| { type: 'token'; delta: string }
	| { type: 'tool_start'; name: string; args?: unknown }
	| { type: 'tool_result'; name: string; success: boolean; result?: unknown }
	// The agent wants to run a tool that needs the user's approval; the turn ends
	// and the client re-sends with the tool name in `approvals` to proceed.
	| { type: 'confirmation_required'; name: string; args?: unknown; tier: string }
	| { type: 'done'; tokens_in?: number; tokens_out?: number; iterations?: number }
	// `code` lets callers pivot on machine-readable reasons (e.g. show a
	// "Configure an AI provider" hint when code === 'no_provider') while still
	// defaulting to the raw message for anything unrecognized.
	| { type: 'error'; message: string; code?: string | null };

export interface ChatStreamAttachment {
	filename: string;
	content_base64: string;
}

export interface ChatStreamRequest {
	message: string;
	conversation_id?: string;
	attachments?: ChatStreamAttachment[];
	approvals?: string[];
	// Which provider answers, and optionally which of its models. Omitted, the
	// turn keeps whatever the conversation already used.
	provider_id?: string;
	model?: string;
}

// Async-generator consumer. Usage:
//   for await (const evt of streamChat(req, ctrl.signal)) { … }
// Aborting the caller's AbortController cleanly closes the reader and ends the
// generator; the backend's cancellation token fires too.
export async function* streamChat(
	request: ChatStreamRequest,
	signal?: AbortSignal
): AsyncGenerator<ChatStreamEvent, void, void> {
	// The SSE fetch bypasses client.ts's auth interceptor, so a near-expired
	// access token would never get refreshed and a long chat session would send
	// an expired JWT. Refresh up front when it's about to lapse, and retry once
	// on a 401 in case it expired between the check and the send.
	const open = () => {
		const headers: Record<string, string> = {
			'Content-Type': 'application/json',
			Accept: 'text/event-stream'
		};
		const token = authStore.accessToken;
		if (token) headers['Authorization'] = `Bearer ${token}`;
		return fetch('/api/ai/chat', {
			method: 'POST',
			headers,
			body: JSON.stringify(request),
			signal
		});
	};

	if (authStore.isExpiringSoon) await tryRefresh();
	let resp = await open();
	if (resp.status === 401 && (await tryRefresh())) {
		resp = await open();
	}

	if (!resp.ok || !resp.body) {
		// Surface the backend error body as a single `error` event so the caller
		// renders it the same way it renders mid-stream failures. Never throws —
		// keeps the UI code path uniform.
		const message = await safeText(resp);
		yield { type: 'error', message: `HTTP ${resp.status}: ${message || resp.statusText}` };
		return;
	}

	const reader = resp.body.getReader();
	const decoder = new TextDecoder('utf-8');
	let buffer = '';

	try {
		while (true) {
			const { done, value } = await reader.read();
			if (done) break;

			buffer += decoder.decode(value, { stream: true });

			// SSE frame boundary is "\n\n": process every complete frame, keep the
			// remainder for the next chunk.
			let boundary = buffer.indexOf('\n\n');
			while (boundary !== -1) {
				const frame = buffer.slice(0, boundary);
				buffer = buffer.slice(boundary + 2);
				const evt = parseFrame(frame);
				if (evt) yield evt;
				boundary = buffer.indexOf('\n\n');
			}
		}
	} finally {
		// Abort leaves the reader locked; release it so the connection can be
		// reclaimed by the HTTP stack.
		try {
			reader.releaseLock();
		} catch {
			/* already released */
		}
	}
}

// Each SSE frame may hold multiple `data:` lines (per spec, joined by newlines).
// Our server sends one, but we stay spec-strict so a re-framing proxy can't break us.
function parseFrame(frame: string): ChatStreamEvent | null {
	const lines = frame.split('\n').filter((l) => l.startsWith('data:'));
	if (lines.length === 0) return null;
	const payload = lines.map((l) => l.slice(5).trimStart()).join('\n');
	if (!payload) return null;
	try {
		return JSON.parse(payload) as ChatStreamEvent;
	} catch {
		// Malformed frame — skip rather than tear the whole stream down.
		return null;
	}
}

async function safeText(resp: Response): Promise<string> {
	try {
		return await resp.text();
	} catch {
		return '';
	}
}
