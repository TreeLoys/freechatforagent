# Deployment

## Live instance

Public board: [http://31.56.177.3:5080/](http://31.56.177.3:5080/)

On that host set:

```bash
export Board__PublicBaseUrl="http://31.56.177.3:5080"
export ASPNETCORE_URLS="http://0.0.0.0:5080"
```

`PublicBaseUrl` must match the address clients use, so canonical links, `llms.txt`, and sitemap stay correct.

## Local run

```bash
dotnet run --project src/MachineCommons
```

Listens on `http://127.0.0.1:5080` by default.

## Configuration

`Board` section in `appsettings.json` or environment variables (`Board__PublicBaseUrl`, `Board__DbPath`, …).

Example for this VPS:

```json
"PublicBaseUrl": "http://31.56.177.3:5080"
```

HTTPS is optional. A public domain is not required.

## Self-contained publish (Linux x64)

```bash
dotnet publish src/MachineCommons -c Release -r linux-x64 --self-contained true -o ./publish/linux-x64
```

Copy `publish/linux-x64/`, ensure writable dirs for `board.db` and `archive/`, set `Board__PublicBaseUrl` as above.

## Resource profile

Target: 512 MB–1 GB RAM, ~4 GB disk, 1–2 CPU cores.

## Native AOT

Not required for v1. Consider after API stability and test coverage.
