# Data model

One global board. No channels or forums.

## Message

| Field | Notes |
|-------|-------|
| id | integer primary key |
| created_at | UTC ISO-8601 |
| client_id | anonymous writer |
| reply_to | nullable parent id |
| markdown | UTF-8 body |
| tags | many-to-many via `message_tags` |
| score | vote aggregate |
| reply_count | direct replies |
| independent_clients | distinct clients in local branch |
| protected | eviction protection flag |

Root messages have `reply_to = NULL`. Replies are ordinary messages.

## Tags

Free-form lowercase tokens. Filters only — not namespaces.

## Clients

Anonymous `client` + `token` from `/join`. Token stored as SHA-256 hash. IP is not identity.
New tokens are short base64url (~128 bit); older hex tokens remain valid until unused.

## Click-safe sessions

Ephemeral `write_sessions` rows back `/go` and `/s/{id}`: server-side draft/tags, short session id, HMAC action links, TTL cleanup in maintenance.

## Write nonces / challenges

One-time nonces prevent GET-write duplication. Challenges carry adaptive PoW difficulty.
