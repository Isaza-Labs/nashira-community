# Messaging webhook signing secret rotation

Only Admins may read or manage `/api/messaging/channels`. Create with `POST`;
update an existing channel with `PUT /api/messaging/channels/{id}`.

* Omit `signing_secret` (or send null) to preserve it on an unrelated edit.
* Supply a fresh provider-issued or cryptographically random `signing_secret` to
  set or rotate it. The response and subsequent GETs contain only
  `has_signing_secret`, never plaintext or ciphertext.
* Send `clear_signing_secret:true` to clear it. Do not also send `signing_secret`.
  Empty strings are rejected. The frontend API client translates an explicitly
  empty `signingSecret` payload into this clear field.

New values must contain 32–4096 characters, no whitespace/control characters,
and at least 8 distinct characters. This rejects obvious weakness; it does not
measure entropy. Existing stored secrets are not rewritten by unrelated edits.
Use the provider's required format as well.

The audit trail records `signing_secret.set`, `signing_secret.rotate`, or
`signing_secret.clear` with actor, channel ID, timestamp and presence boolean.
It contains no secret value. The secret mutation and required audit event use a
single database save; an audit write failure fails the operation. Ordinary API
mutation auditing remains in place too.

Rotation replaces the old value immediately, with no dual-key grace period.
Coordinate the provider's webhook configuration separately. Clearing a key does
not permit unsigned delivery: missing/mismatched signatures remain rejected,
including on channels whose legacy `allow_unsigned` flag is true.
