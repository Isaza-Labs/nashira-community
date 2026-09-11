// Knowledge-base CRUD. Reads are Viewer; create/update/delete are Operator. The
// same generic CRUD shape as devices — `Article` is a `type` alias for DataTable.

import { api, type ListResponse } from '$lib/api/client';

export type Article = {
	id: string;
	title: string;
	slug: string;
	content: string;
	tags: string[];
	createdAt: string;
	updatedAt: string;
};

export interface ArticlePayload {
	title: string;
	content: string;
	tags: string[];
}

interface ArticleShape {
	knowledge_article_id: string;
	title: string;
	slug: string;
	content: string;
	tags: string[];
	created_at: string;
	updated_at: string;
}

function toArticle(a: ArticleShape): Article {
	return {
		id: a.knowledge_article_id,
		title: a.title,
		slug: a.slug,
		content: a.content,
		tags: a.tags ?? [],
		createdAt: a.created_at,
		updatedAt: a.updated_at
	};
}

function toBody(p: ArticlePayload) {
	return { title: p.title, content: p.content, tags: p.tags };
}

export async function listArticles(limit = 100, offset = 0): Promise<ListResponse<Article>> {
	const res = await api<ListResponse<ArticleShape>>(`/knowledge?limit=${limit}&offset=${offset}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toArticle) };
}

export async function createArticle(p: ArticlePayload): Promise<Article> {
	return toArticle(await api<ArticleShape>('/knowledge', { method: 'POST', body: JSON.stringify(toBody(p)) }));
}

export async function updateArticle(id: string, p: ArticlePayload): Promise<Article> {
	return toArticle(
		await api<ArticleShape>(`/knowledge/${id}`, { method: 'PUT', body: JSON.stringify(toBody(p)) })
	);
}

export async function deleteArticle(id: string): Promise<void> {
	await api<ArticleShape>(`/knowledge/${id}`, { method: 'DELETE' });
}
