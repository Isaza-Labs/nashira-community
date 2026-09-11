# Skill: email mailboxes — reading and managing mail

This capability covers outbound delivery and the inbound mailbox side:
the mailbox behind an **email channel** that has an IMAP host configured. Channels
without one are outbound-only, and every mailbox tool will say so — the fix is
`/admin/email`, not retrying.

Tools: `list_email_folders`, `list_emails`, `read_email`, `mark_email`, `move_email`,
`archive_email`, `delete_email`. All take an optional `channel` (slug or id); omitted,
the oldest enabled channel with IMAP configured is used, deterministically.

## The working loop

1. `list_emails` — newest first, with `unread_only`, `from_contains`,
   `subject_contains`, `text_contains`, `since_days`, `limit` (max 100), and `folder`
   (default INBOX). Every message comes back with a `uid`.
2. `read_email` with one `uid` for the full message: headers, text/html body (capped,
   `body_truncated` says when), attachment names and sizes.
3. Act on uids: `mark_email` (read/unread), `move_email` (`target_folder` must exist —
   `list_email_folders` shows what does), `archive_email`, `delete_email`.

Uids are per folder and can go stale: if an action reports fewer affected than
requested, the missing messages were moved or deleted since the listing — list again
rather than assuming failure.

## What reading does and does not do

Reading is genuinely read-only: `read_email` does **not** mark the message seen — the
user's unread state belongs to them. When triage means marking handled mail as read,
do it explicitly with `mark_email`.

## Deleting

`delete_email` moves to the server's Trash by default, which the user can undo in their
mail client. `permanent: true` expunges immediately and nothing can bring the message
back — also the behaviour when the account has no Trash folder; the result's `outcome`
says which happened. Never choose `permanent` on your own initiative: default to Trash
unless the user explicitly asked for permanent deletion.

This is someone's real mailbox. Summarize what a bulk action will touch (count, folder,
filter) before confirming it, and prefer archive over delete when the user's words are
ambiguous ("clean up", "get rid of the noise").

## Sending email

`send_email` needs `to`, `subject`, `body`; optional `cc` and `is_html`. It requires an enabled SMTP email channel
(`/api/email/channels`: host, port, security, from address, default recipients).

Email leaves the system and cannot be recalled. Before sending, show the recipients and subject, then obtain confirmation.

## Messaging channels

Outbound Slack or Teams style webhooks (`/api/notifications/channels`) are an external side
effect. Use the configured channel rather than inventing a URL, keep the payload concise, and
confirm before sending. Delivery checks and delivery history are available through the
`na_notifications` spec when this capability is enabled.
