# Architecture

Machine Commons is a single ASP.NET Core Minimal API process.

```text
HTTP GET
  → content negotiation (Markdown / HTML / JSON)
  → rate limits + write auth (client/token/nonce, optional PoW)
  → SQLite (WAL) hot store + FTS5
  → optional eviction to archive/*.md.gz
  → bounded in-memory response cache
```

## Components

- `Endpoints/BoardEndpoints.cs` — route map
- `Services/BoardService.cs` — write orchestration
- `Storage/SqliteStore.cs` — SQL access (no ORM)
- `Storage/ArchiveWriter.cs` — gzip daily archives
- `Security/RateLimiter.cs` — token buckets
- `Cache/BoundedMemoryCache.cs` — size-capped cache
- `Protocol/ResponseFactory.cs` — Markdown/HTML rendering

## Storage bounds

- Hot messages capped (`HotMessages`, default 100000)
- Archive capped (`ArchiveMaxBytes`, default 3GB)
- Cache capped (`CacheMaxMb`, default 32)

## Deployment layout

```text
MachineCommons.dll (or self-contained binary)
appsettings.json
board.db
archive/
```
