// Chat sessions: one transcript + streaming state machine PER CONVERSATION, held
// in a hub keyed by conversation id — so any number of chats can stream at once
// and switching threads never touches another thread's in-flight turn. send()
// drives the SSE generator through a PURE REDUCER (apply) that rebuilds the
// assistant message per event — required for Svelte 5 `$state`.

import { streamChat, type ChatStreamEvent } from '$lib/api/ai-stream';
import type { StoredMessage } from '$lib/api/conversations.api';

export type Role = 'user' | 'assistant';
export type ToolStatus = 'running' | 'ok' | 'failed';

export interface ToolInvocation {
	name: string;
	status: ToolStatus;
	args?: unknown;
	result?: unknown;
}

// A file attached to a turn; content is base64 (no data: prefix).
export interface ChatAttachment {
	filename: string;
	contentBase64: string;
}

// A tool the agent wants to run that needs the user's approval (governance C).
export interface PendingConfirmation {
	toolName: string;
	tier: string;
	args?: unknown;
	resolved: boolean;
}

export interface ChatMessage {
	id: number;
	role: Role;
	content: string;
	tools: ToolInvocation[];
	attachments: string[]; // filenames, shown on the user bubble
	confirmation?: PendingConfirmation;
	streaming: boolean;
	error?: string;
}

export class ChatSession {
	conversationId = $state<string | null>(null);
	messages = $state<ChatMessage[]>([]);
	sending = $state(false);

	// Which provider/model this thread talks to. Null means "not chosen here", and
	// the server resolves the tenant default — so a fresh chat needs no selection
	// to work. The server echoes what it resolved on every turn (the `model`
	// event), which is what fills these in for a thread that never picked.
	providerId = $state<string | null>(null);
	model = $state<string | null>(null);
	// The provider's display name, for the selector's label. For a stored thread
	// whose provider row was renamed, only the server knows the current one.
	providerName = $state<string | null>(null);

	private nextId = 1;
	private controller: AbortController | null = null;

	// Replace the transcript with the server's stored history. Never while a turn
	// is streaming: the live transcript is strictly newer than what the server has
	// persisted, and the old stop()-first behavior here is what used to kill a
	// running turn just for navigating back to it.
	setHistory(
		id: string,
		history: StoredMessage[],
		choice?: { providerId: string | null; model: string | null }
	) {
		if (this.sending) return;
		this.conversationId = id;
		if (choice) {
			this.providerId = choice.providerId;
			this.model = choice.model;
		}
		this.messages = history.map((m) => ({
			id: this.nextId++,
			role: m.role,
			content: m.content,
			tools: [],
			attachments: m.attachments ?? [],
			streaming: false
		}));
	}

	/**
	 * Point this thread at another model. Takes effect on the next turn: the
	 * choice rides with the request and the server persists it on the
	 * conversation, so follow-ups keep it without re-sending.
	 */
	selectModel(providerId: string, model: string, providerName?: string) {
		this.providerId = providerId;
		this.model = model;
		this.providerName = providerName ?? null;
	}

	stop() {
		this.controller?.abort();
		this.controller = null;
		this.sending = false;
		this.messages = this.messages.map((m) => (m.streaming ? { ...m, streaming: false } : m));
	}

	async send(text: string, opts: { attachments?: ChatAttachment[]; approvals?: string[] } = {}) {
		const trimmed = text.trim();
		const attachments = opts.attachments ?? [];
		// `sending` is per-conversation: it stops a second turn racing THIS thread's
		// history (the backend persists a turn with read-modify-write), while other
		// conversations stream freely in their own sessions.
		if ((!trimmed && attachments.length === 0) || this.sending) return;

		const user: ChatMessage = {
			id: this.nextId++,
			role: 'user',
			content: trimmed,
			tools: [],
			attachments: attachments.map((a) => a.filename),
			streaming: false
		};
		const assistant: ChatMessage = {
			id: this.nextId++,
			role: 'assistant',
			content: '',
			tools: [],
			attachments: [],
			streaming: true
		};
		this.messages = [...this.messages, user, assistant];
		const aid = assistant.id;

		const controller = new AbortController();
		this.controller = controller;
		this.sending = true;

		try {
			for await (const evt of streamChat(
				{
					message: trimmed,
					conversation_id: this.conversationId ?? undefined,
					attachments: attachments.map((a) => ({
						filename: a.filename,
						content_base64: a.contentBase64
					})),
					approvals: opts.approvals,
						// Only when this thread has actually chosen. Sending nulls
						// would be indistinguishable from a pick and would stop the
						// server falling back to the conversation's own value.
						provider_id: this.providerId ?? undefined,
						model: this.model ?? undefined
				},
				controller.signal
			)) {
				this.apply(aid, evt);
			}
		} catch {
			if (!controller.signal.aborted) {
				this.patch(aid, (m) => ({
					...m,
					error: 'The connection was interrupted. Please try again.',
					streaming: false
				}));
			}
		} finally {
			if (this.controller === controller) this.controller = null;
			this.patch(aid, (m) => (m.streaming ? { ...m, streaming: false } : m));
			this.sending = false;
		}
	}

	// Approve a pending tool and re-run the turn with it authorized.
	approve(messageId: number, toolName: string) {
		this.patch(messageId, (m) =>
			m.confirmation ? { ...m, confirmation: { ...m.confirmation, resolved: true } } : m
		);
		void this.send('Yes, please proceed.', { approvals: [toolName] });
	}

	// Dismiss a pending confirmation without running the tool.
	dismissConfirmation(messageId: number) {
		this.patch(messageId, (m) =>
			m.confirmation ? { ...m, confirmation: { ...m.confirmation, resolved: true } } : m
		);
	}

	// Pure reducer: fold one event into the assistant message `aid`.
	private apply(aid: number, evt: ChatStreamEvent) {
		switch (evt.type) {
			case 'conversation':
				this.conversationId = evt.conversation_id;
				break;
			case 'model':
				// What actually answered, which is not always what was asked for: a
				// turn that named nothing gets the conversation's stored choice or
				// the tenant default back, and the selector should show that.
				this.providerId = evt.ai_provider_id;
				this.model = evt.model;
				this.providerName = evt.provider;
				break;
			case 'token':
				this.patch(aid, (m) => ({ ...m, content: m.content + evt.delta }));
				break;
			case 'tool_start':
				this.patch(aid, (m) => ({
					...m,
					tools: [...m.tools, { name: evt.name, status: 'running', args: evt.args }]
				}));
				break;
			case 'tool_result':
				this.patch(aid, (m) => ({ ...m, tools: markDone(m.tools, evt.name, evt.success, evt.result) }));
				break;
			case 'confirmation_required':
				this.patch(aid, (m) => ({
					...m,
					streaming: false,
					confirmation: { toolName: evt.name, tier: evt.tier, args: evt.args, resolved: false }
				}));
				break;
			case 'done':
				this.patch(aid, (m) => ({ ...m, streaming: false }));
				break;
			case 'error':
				this.patch(aid, (m) => ({ ...m, error: evt.message, streaming: false }));
				break;
		}
	}

	private patch(id: number, fn: (m: ChatMessage) => ChatMessage) {
		this.messages = this.messages.map((m) => (m.id === id ? fn(m) : m));
	}
}

// Resolve the most recent still-running call of this name to its final status.
function markDone(
	tools: ToolInvocation[],
	name: string,
	success: boolean,
	result: unknown
): ToolInvocation[] {
	const next = [...tools];
	for (let i = next.length - 1; i >= 0; i--) {
		if (next[i].name === name && next[i].status === 'running') {
			next[i] = { ...next[i], status: success ? 'ok' : 'failed', result };
			return next;
		}
	}
	return next;
}

// One live session per conversation for the lifetime of the tab. The map is the
// whole point: navigating away from a streaming chat parks its session here,
// still streaming, and navigating back picks up the same instance.
class ChatHub {
	private sessions = new Map<string, ChatSession>();
	private draft = new ChatSession();

	/**
	 * The session a route shows: `null` is the blank draft. A draft that acquired
	 * its server id mid-stream is re-keyed under that id the moment the URL
	 * catches up — same live instance, nothing reloaded, nothing aborted — and a
	 * fresh draft takes its place for the next "New chat".
	 */
	sessionFor(id: string | null): ChatSession {
		if (!id) return this.draft;
		const existing = this.sessions.get(id);
		if (existing) return existing;
		if (this.draft.conversationId === id) {
			const promoted = this.draft;
			this.sessions.set(id, promoted);
			this.draft = new ChatSession();
			return promoted;
		}
		const created = new ChatSession();
		created.conversationId = id;
		this.sessions.set(id, created);
		return created;
	}

	/**
	 * "New chat" while already on the blank route. A draft that already owns a
	 * conversation id keeps streaming in the background under that id; one that
	 * never got an id cannot be revisited, so its turn is stopped honestly rather
	 * than left streaming into an unreachable transcript.
	 */
	freshDraft(): ChatSession {
		const current = this.draft;
		if (current.messages.length === 0 && !current.sending) return current;
		if (current.conversationId) this.sessions.set(current.conversationId, current);
		else current.stop();
		this.draft = new ChatSession();
		// Carry the model choice into the new chat. Someone who switched to a
		// specific model to do a piece of work is still doing that work on the
		// next thread; silently reverting to the default would undo a deliberate
		// choice on every "New chat".
		if (current.providerId && current.model) {
			this.draft.selectModel(current.providerId, current.model, current.providerName ?? undefined);
		}
		return this.draft;
	}
}

export const chatHub = new ChatHub();
