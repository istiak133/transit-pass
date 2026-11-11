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
./deploy/dev-secrets.sh          # once: random DB passwords into .env (git-ignored)
docker compose up --build
curl -i http://localhost:5100/health/live
# Port 5432 already taken on your machine? POSTGRES_HOST_PORT=55432 docker compose up --build
```

Running one service with `dotnet run` against the Compose database? Give it the connection string
through user secrets, never through `appsettings.json`:

```bash
dotnet user-secrets --project src/Services/Identity/TransitPass.Identity.Api init
dotnet user-secrets --project src/Services/Identity/TransitPass.Identity.Api \
  set ConnectionStrings:Default "Host=localhost;Database=identity_db;Username=identity_svc;Password=<from .env>"
```

Build and test (needs the .NET SDK from `global.json`, and Docker running for the database tests):

```bash
dotnet test
```

## Database migrations

Each service owns its schema through EF Core migrations, stored in the service under `Data/Migrations`.

```bash
dotnet tool restore                                   # installs the pinned dotnet-ef
dotnet ef migrations add <Name> \
  --project src/Services/Network/TransitPass.Network.Api --output-dir Data/Migrations
```

- **Development:** `Database:MigrateOnStartup` is `true`, so the service applies pending migrations when it starts.
- **Production:** it stays off. Migrations run once as a deploy step (`dotnet ef migrations bundle`), so several instances never race to change the schema.
- **Tests** run against a real PostgreSQL 17 started by Testcontainers (Docker required), with the same migrations.
