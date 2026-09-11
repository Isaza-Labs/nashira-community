// Vendor command catalog (`/api/vendor-commands`). Maps a vendor-neutral intent
// plus a platform to the CLI command that expresses it, which is what lets one
// workflow run on Cisco, Juniper and Nokia.

import { api, type ListResponse } from '$lib/api/client';

export type VendorCommand = {
	id: string;
	intent: string;
	platform: string;
	command: string;
	description: string | null;
	readOnly: boolean;
	parserTemplate: string | null;
	updatedAt: string;
};

export interface VendorCommandPayload {
	intent: string;
	platform: string;
	command: string;
	description: string;
	readOnly: boolean;
	parserTemplate: string;
}

interface CommandShape {
	vendor_command_id: string;
	intent: string;
	platform: string;
	command: string;
	description: string | null;
	read_only: boolean;
	parser_template: string | null;
	updated_at: string;
}

function toCommand(c: CommandShape): VendorCommand {
	return {
		id: c.vendor_command_id,
		intent: c.intent,
		platform: c.platform,
		command: c.command,
		description: c.description,
		readOnly: c.read_only,
		parserTemplate: c.parser_template,
		updatedAt: c.updated_at
	};
}

function toBody(p: VendorCommandPayload) {
	return {
		intent: p.intent,
		platform: p.platform,
		command: p.command,
		description: p.description || null,
		read_only: p.readOnly,
		parser_template: p.parserTemplate.trim() || null
	};
}

// A platform the deployment knows about. `commandCount` is what the catalogue
// covers; `deviceCount` is what the inventory actually runs. A platform with
// devices and no commands is the gap worth seeing — every intent resolves to a
// 404 on those boxes.
export type VendorPlatform = {
	platform: string;
	commandCount: number;
	deviceCount: number;
};

interface PlatformShape {
	platform: string;
	command_count: number;
	device_count: number;
}

export async function listVendorCommands(
	options: { platform?: string; intent?: string; limit?: number; offset?: number } = {}
): Promise<ListResponse<VendorCommand>> {
	const q = new URLSearchParams({
		limit: String(options.limit ?? 200),
		offset: String(options.offset ?? 0)
	});
	// Filtering server-side rather than in the browser: the catalogue grows one
	// row per intent per platform, so a deployment with a dozen vendors runs past
	// any page size long before it runs out of intents.
	if (options.platform) q.set('platform', options.platform);
	if (options.intent) q.set('intent', options.intent);

	const res = await api<ListResponse<CommandShape>>(`/vendor-commands?${q}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toCommand) };
}

// Every platform in the catalogue or on a device, derived server-side — the
// picker cannot go stale against a vendor YAML added since the frontend built.
export async function listVendorPlatforms(): Promise<VendorPlatform[]> {
	const res = await api<ListResponse<PlatformShape>>('/vendor-commands/platforms');
	return res.items.map((p) => ({
		platform: p.platform,
		commandCount: p.command_count,
		deviceCount: p.device_count
	}));
}

export async function createVendorCommand(p: VendorCommandPayload): Promise<VendorCommand> {
	return toCommand(
		await api<CommandShape>('/vendor-commands', { method: 'POST', body: JSON.stringify(toBody(p)) })
	);
}

// Intent and platform are the key and are not editable — changing them would be a
// different row, and rewriting them in place silently changes what a workflow
// resolves.
export async function updateVendorCommand(id: string, p: VendorCommandPayload): Promise<VendorCommand> {
	return toCommand(
		await api<CommandShape>(`/vendor-commands/${id}`, { method: 'PUT', body: JSON.stringify(toBody(p)) })
	);
}

export async function deleteVendorCommand(id: string): Promise<void> {
	await api<CommandShape>(`/vendor-commands/${id}`, { method: 'DELETE' });
}
