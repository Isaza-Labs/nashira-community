// Email channels (`/api/email/channels`, Admin). The password is write-only:
// omit it on update to keep the stored value, `clear_password` removes it.

import { api, type ListResponse } from '$lib/api/client';

export const EMAIL_SECURITY_MODES = ['starttls', 'ssl', 'none'] as const;

export type EmailChannel = {
	id: string;
	name: string;
	slug: string;
	description: string | null;
	host: string;
	port: number;
	security: string;
	username: string | null;
	hasPassword: boolean;
	fromAddress: string;
	fromName: string | null;
	defaultRecipients: string | null;
	enabled: boolean;
	imapHost: string | null;
	imapPort: number;
	imapSecurity: string;
	imapUsername: string | null;
	hasImapPassword: boolean;
	imapConfigured: boolean;
	updatedAt: string;
};

export interface EmailChannelPayload {
	name: string;
	description: string;
	host: string;
	port: number;
	security: string;
	username: string;
	password: string;
	clearPassword: boolean;
	fromAddress: string;
	fromName: string;
	defaultRecipients: string;
	enabled: boolean;
	// Inbound (IMAP). Empty imapHost = outbound-only channel; empty
	// imapUsername/imapPassword = reuse the SMTP credentials.
	imapHost: string;
	imapPort: number;
	imapSecurity: string;
	imapUsername: string;
	imapPassword: string;
	clearImapPassword: boolean;
}

interface ChannelShape {
	email_channel_id: string;
	name: string;
	slug: string;
	description: string | null;
	host: string;
	port: number;
	security: string;
	username: string | null;
	has_password: boolean;
	from_address: string;
	from_name: string | null;
	default_recipients: string | null;
	enabled: boolean;
	imap_host: string | null;
	imap_port: number;
	imap_security: string;
	imap_username: string | null;
	has_imap_password: boolean;
	imap_configured: boolean;
	updated_at: string;
}

function toChannel(c: ChannelShape): EmailChannel {
	return {
		id: c.email_channel_id,
		name: c.name,
		slug: c.slug,
		description: c.description,
		host: c.host,
		port: c.port,
		security: c.security,
		username: c.username,
		hasPassword: c.has_password,
		fromAddress: c.from_address,
		fromName: c.from_name,
		defaultRecipients: c.default_recipients,
		enabled: c.enabled,
		imapHost: c.imap_host,
		imapPort: c.imap_port,
		imapSecurity: c.imap_security,
		imapUsername: c.imap_username,
		hasImapPassword: c.has_imap_password,
		imapConfigured: c.imap_configured,
		updatedAt: c.updated_at
	};
}

function toBody(p: EmailChannelPayload, isUpdate: boolean) {
	return {
		name: p.name,
		description: p.description || null,
		host: p.host,
		port: p.port,
		security: p.security,
		username: p.username || null,
		// Empty means "leave the stored password alone" on update.
		password: p.password || null,
		...(isUpdate && p.clearPassword ? { clear_password: true } : {}),
		from_address: p.fromAddress,
		from_name: p.fromName || null,
		default_recipients: p.defaultRecipients || null,
		enabled: p.enabled,
		// On update the backend treats an explicit "" as "clear the inbound side",
		// which is exactly what an emptied field should do.
		imap_host: isUpdate ? p.imapHost : p.imapHost || null,
		imap_port: p.imapPort,
		imap_security: p.imapSecurity,
		imap_username: p.imapUsername || null,
		imap_password: p.imapPassword || null,
		...(isUpdate && p.clearImapPassword ? { clear_imap_password: true } : {})
	};
}

export async function listEmailChannels(limit = 100): Promise<ListResponse<EmailChannel>> {
	const res = await api<ListResponse<ChannelShape>>(`/email/channels?limit=${limit}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toChannel) };
}

export async function createEmailChannel(p: EmailChannelPayload): Promise<EmailChannel> {
	return toChannel(
		await api<ChannelShape>('/email/channels', { method: 'POST', body: JSON.stringify(toBody(p, false)) })
	);
}

export async function updateEmailChannel(id: string, p: EmailChannelPayload): Promise<EmailChannel> {
	return toChannel(
		await api<ChannelShape>(`/email/channels/${id}`, {
			method: 'PUT',
			body: JSON.stringify(toBody(p, true))
		})
	);
}

export async function deleteEmailChannel(id: string): Promise<void> {
	await api<ChannelShape>(`/email/channels/${id}`, { method: 'DELETE' });
}

// Sends a real email — the only honest test of an SMTP relay.
export async function testEmailChannel(id: string, to?: string): Promise<string[]> {
	const r = await api<{ sent: boolean; to: string[] }>(`/email/channels/${id}/test`, {
		method: 'POST',
		body: JSON.stringify({ to: to || null })
	});
	return r.to;
}

export type ImapTestResult = { folders: number; inboxMessages: number; inboxUnread: number };

// Connects and lists folders — the equivalent honest test for the IMAP side.
export async function testEmailChannelImap(id: string): Promise<ImapTestResult> {
	const r = await api<{ ok: boolean; folders: number; inbox_messages: number; inbox_unread: number }>(
		`/email/channels/${id}/test-imap`,
		{ method: 'POST' }
	);
	return { folders: r.folders, inboxMessages: r.inbox_messages, inboxUnread: r.inbox_unread };
}
