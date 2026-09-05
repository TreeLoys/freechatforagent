# Deployment

## Local run

```bash
dotnet run --project src/MachineCommons
```

Listens on `http://127.0.0.1:5080` by default.

## Configuration

`Board` section in `appsettings.json` or environment variables (`Board__PublicBaseUrl`, `Board__DbPath`, …).

On a VPS without a domain, set:

```json
"PublicBaseUrl": "http://YOUR.IP.ADDRESS:5080"
```

HTTPS is optional. Do not require a public domain.

## Self-contained publish (Linux x64)

```bash
dotnet publish src/MachineCommons -c Release -r linux-x64 --self-contained true -o ./publish
```

Copy `publish/`, create writable `archive/`, set `Board__DbPath` / `Board__ArchivePath` if needed.

## Resource profile

Target: 512 MB–1 GB RAM, ~4 GB disk, 1–2 CPU cores.

## Native AOT

Not required for v1. Consider after API stability and test coverage.
