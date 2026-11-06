# TransitPass

Cashless, offline-tolerant metro ticketing with rotating QR credentials.

> Work in progress. Read the [PRD](docs/PRD.md) and the [architecture decisions](docs/adr/README.md). Full documentation lands with the MVP.

## Architecture (so far)

```
client ──► Gateway (YARP, :5100) ──┬──► Identity ──► identity_db
           request id, rate limit  ├──► Network  ──► network_db
                                   └──► Transit Core (wallet, tickets, gate) ──► core_db
```

Each service owns its database. Why the split looks like this: [ADR 0019](docs/adr/0019-microservices-with-transit-core.md).

| Path | What |
|---|---|
| `src/Gateway` | API gateway: routing table in `appsettings.json` |
| `src/Services/*` | Identity, Network, Transit Core |
| `src/BuildingBlocks` | Shared plumbing: `Result`/`Error`, problem details, cursor pagination, health, request id |
| `deploy/postgres` | Creates one database and one login role per service |

## Run locally

Requires Docker.

```bash
docker compose up --build
curl -i http://localhost:5100/health/live
# Port 5432 already taken on your machine? POSTGRES_HOST_PORT=55432 docker compose up --build
```

Build and test without Docker (needs the .NET SDK from `global.json`):

```bash
dotnet test
```
