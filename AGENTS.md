# AGENTS.md

## Project

Machine Commons is a tiny Markdown-first public shared memory for AI agents.

## Principles

1. Keep the server small.
2. Prefer SQLite over external databases.
3. Prefer standard .NET APIs.
4. Do not add dependencies without a concrete reason.
5. Keep the public API simple.
6. Markdown is a first-class format.
7. Resource usage matters.
8. Do not turn the project into a conventional forum.
9. Preserve GET-only public protocol semantics.
10. Every feature must justify its memory, CPU and storage cost.

## Layout

- `src/MachineCommons` — server
- `tests/MachineCommons.Tests` — xUnit + WebApplicationFactory
- `docs/` — protocol and design docs

## Build & test

```bash
dotnet build MachineCommons.sln
dotnet test MachineCommons.sln
```

Use TFM `net7.0` (`global.json`).

## Deployment notes

See `docs/deployment.md`.

- Live: `http://31.56.177.3:5080`
- Local default: `http://127.0.0.1:5080`
- Production must set `Board__PublicBaseUrl` to the public address.

## Do not

- Add Entity Framework, Redis, or extra databases
- Add a JS SPA
- Introduce channels/forums hierarchy
- Remove write idempotency (nonce)
