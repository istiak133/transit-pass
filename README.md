<h1 align="center">🚇 TransitPass</h1>
<p align="center"><em>Cashless metro ticketing for Dhaka. Tap in, ride, tap out. No double spend, no copied QR, no lost money.</em></p>

<p align="center">
  <a href="https://github.com/istiak133/transit-pass/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/istiak133/transit-pass/ci.yml?branch=main&style=flat-square&label=CI" alt="CI"/></a>
  <img src="https://img.shields.io/badge/tests-117%20passing-2ea44f?style=flat-square" alt="Tests"/>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 10"/>
  <img src="https://img.shields.io/badge/ASP.NET%20Core-Minimal%20APIs-512BD4?style=flat-square" alt="ASP.NET Core"/>
  <img src="https://img.shields.io/badge/PostgreSQL-17-4169E1?style=flat-square&logo=postgresql&logoColor=white" alt="PostgreSQL 17"/>
  <img src="https://img.shields.io/badge/EF%20Core-10-6C3FC5?style=flat-square" alt="EF Core"/>
  <img src="https://img.shields.io/badge/gateway-YARP-0078D4?style=flat-square" alt="YARP"/>
  <img src="https://img.shields.io/badge/Docker-Compose-2496ED?style=flat-square&logo=docker&logoColor=white" alt="Docker"/>
</p>

---

A metro ticketing backend built around one passenger, **Rafi**: he signs up, tops up his wallet, buys a ticket from **Uttara North** to **Motijheel**, taps in at the gate, rides, and taps out.

- 🏗️ **Architecture:** four .NET 10 services behind a YARP API gateway, each with its own PostgreSQL database
- ✅ **Built so far:** the platform (gateway, rate limiting, errors, health, CI), stations, the track graph with fare quotes (Dijkstra), and user registration
- 🛣️ **In progress:** login with ES256 JWT, then wallet and ledger, top-ups, tickets, signed QR, gates (see [Roadmap](#-roadmap))
- 🧪 **Quality:** 117 tests, database tests against a real PostgreSQL 17 (Testcontainers), and CI with CodeQL on every pull request
- 📚 **Decisions:** 19 [Architecture Decision Records](docs/adr/README.md) and a [PRD](docs/PRD.md)

---

## 📑 Contents

[Problem](#-problem-statement) · [Features](#-features-implemented) · [Architecture](#-architecture) · [Data model](#️-data-model) · [Tech stack](#-tech-stack) · [Project structure](#-project-structure) · [Setup](#-setup) · [Tests](#-tests) · [API](#-api-overview) · [Fares](#-fare-model) · [Concurrency](#-concurrency) · [Security](#️-security-basics) · [Decisions](#-key-decisions-and-trade-offs) · [Limitations](#-known-limitations) · [Roadmap](#-roadmap) · [How it's built](#-how-its-built)

---

## 🎯 Problem statement

TransitPass started from studying an open-source student metro ticketing project. It works in a demo, but it breaks as soon as real money, real concurrency or a dishonest user shows up:

- 💸 **Double spend:** two purchases at the same instant both read a ৳100 balance and both succeed.
- 🔁 **Payment replay:** top-ups were credited from the browser's "success" redirect, which anyone can repeat or fake.
- 🚪 **Open scanner:** the gate API had no device authentication, so any client could mark tickets as used.
- 📸 **Static QR:** a screenshot shared on WhatsApp let several people ride on one ticket.
- 📴 **No offline mode:** gates without internet stopped everyone.
- 🧍 **Stuck passengers:** a missed exit tap left a journey open forever.
- 🧮 **Float money and mutable balances:** rounding errors, and no record of where money came from.
- 🧪 **No tests, broken CI:** nothing proved any of it worked.

**The goal:** make each of these failures impossible by design, and prove it with tests.

## ✨ Features implemented

**🌐 Platform (all services)**
- One entry point: a **YARP** gateway routes `/api/v1/{identity|network|wallet|tickets|gate}/…` to the right service
- **Rate limiting** per client IP (token bucket: burst 100, refill 50/s); excess requests get `429` with `Retry-After`
- Every error is an **RFC 9457 problem** with a stable machine-readable `code` and a `traceId`
- `X-Request-Id` on every request: accepted only if it is a UUID, otherwise a new one is made (no log injection)
- `/health/live` (process up) and `/health/ready` (database answers `SELECT 1`, else `503`)
- Cursor (keyset) pagination primitives, ready for history screens

**🗺️ Network service**
- Station list and lookup, stored in PostgreSQL through EF Core
- The **track graph**: one row per stretch of track, with its length in metres
- **Fare quotes:** shortest distance by **Dijkstra**, precomputed for every pair of stations at first use, then priced by distance slab

**🔐 Identity service**
- **Sign-up** with a Bangladeshi mobile number, a name and a password
- Phone numbers are **normalized to E.164**: `01711000001`, `8801711000001` and `+880 1711-000001` are one phone, so one account
- Passwords hashed with **PBKDF2-HMAC-SHA256**, 600,000 iterations and a random salt per user, never stored or returned
- **One phone, one account,** even when sign-ups race (tested with 10 at once)

**🛠️ Engineering**
- Database-per-service: each service logs in as its own PostgreSQL role, and the others cannot even connect
- One-command local run with Docker Compose. Images are multi-stage and run as a non-root user
- EF Core migrations per service. The database refuses bad data by itself through CHECK, UNIQUE and FOREIGN KEY constraints

## 📐 Architecture

```mermaid
flowchart LR
    C["Client<br/>(app, gate, curl)"] -->|HTTPS| G["Gateway · YARP :5100<br/>routing · rate limit · request id"]
    G -->|/api/v1/identity| I["Identity service :5101<br/>users · passwords · (next: JWT)"]
    G -->|/api/v1/network| N["Network service :5102<br/>stations · track graph · fares"]
    G -->|/api/v1/wallet · tickets · gate| T["Transit Core :5103<br/>(next: wallet · tickets · gate)"]
    I --> IDB[("identity_db<br/>role identity_svc")]
    N --> NDB[("network_db<br/>role network_svc")]
    T --> CDB[("core_db<br/>role core_svc")]
```

- 🧱 **Four processes, three databases.** Each service owns its data, and no service reads another's tables.
- 💰 **Why wallet, tickets and gates share one service (Transit Core):** buying a ticket must take the money, record it in the ledger and create the ticket **all together or not at all**. Inside one database that is a single ACID transaction. Split across services, it would need sagas and compensations, for no benefit at this size. See [ADR 0019](docs/adr/0019-microservices-with-transit-core.md).
- 🔗 **Across services there are no foreign keys.** Core will store `user_id` taken from a verified token, and copy the stations and fare into the ticket at purchase (a snapshot), so a later price change never alters an old ticket or its refund.

### 🔁 One request, end to end

`GET /api/v1/network/fares/quote?from=uttara-north&to=motijheel`

1. **Gateway:** checks the client's rate-limit bucket, adds an `X-Request-Id`, and forwards to Network
2. **Network:** on first use, `FareCatalog` loads stations and segments from `network_db` once, and runs Dijkstra from every station
3. **Lookup:** two dictionary reads plus one array read give 16,500 m
4. **Price:** a binary search over the fare slabs gives ৳100
5. **Response:** `{"from":"uttara-north","to":"motijheel","distanceM":16500,"farePoisha":10000}`

## 🗄️ Data model

```mermaid
erDiagram
    STATIONS ||--o{ SEGMENTS : "station_a"
    STATIONS ||--o{ SEGMENTS : "station_b"

    USERS {
        uuid id PK "UUIDv7"
        varchar phone "unique, CHECK E.164"
        varchar name
        varchar password_hash "PBKDF2, never plain"
        timestamptz created_at
    }
    STATIONS {
        varchar id PK "slug, CHECK format"
        varchar name "unique"
    }
    SEGMENTS {
        varchar station_a PK "FK, alphabetically smaller"
        varchar station_b PK "FK, alphabetically larger"
        int distance_m "CHECK > 0"
    }
```

`users` lives in `identity_db`. `stations` and `segments` live in `network_db`.

### 📋 Tables

| Table | Database | What it holds | Keys, constraints and indexes |
|---|---|---|---|
| `users` | identity_db | One row per passenger: phone, name, password hash | `pk_users (id)` · `ux_users_phone` unique · `ck_users_phone_e164` CHECK `^\+[1-9][0-9]{7,14}$` |
| `stations` | network_db | The stations, with readable ids like `agargaon` | `pk_stations (id)` · `ux_stations_name` unique · `ck_stations_id_slug` CHECK |
| `segments` | network_db | One row per stretch of track and its length | `pk_segments (station_a, station_b)` · 2 FKs → `stations` with `ON DELETE RESTRICT` · `ck_segments_canonical_order` CHECK `station_a < station_b` (byte collation) · `ck_segments_distance_positive` · `ix_segments_station_b` |

- 💰 Money is **integer poisha** (৳1 = 100), never a float.
- 🕒 Times are `timestamptz` (stored in UTC). User ids are **UUIDv7**: unique without coordination, and time-ordered, so new rows land at the right edge of the B+ tree index.
- ↔️ Track runs both ways, so each stretch is stored **once**. The canonical-order CHECK blocks both reversed duplicates and self-loops. It compares bytes (`COLLATE "C"`), because a language-aware collation can ignore hyphens and disagree with C#'s ordinal order.
- 📇 PostgreSQL does not index foreign key columns, so `ix_segments_station_b` exists. `station_a` is already covered as the first column of the primary key.

**Next tables** (designed, built phase by phase): `wallets`, `ledger_entries`, `topups`, `tickets`, `journeys`, `gate_taps`, `gate_devices`, idempotency records.

## 🧰 Tech stack

| Layer | Choice |
|---|---|
| Runtime and API | .NET 10, ASP.NET Core Minimal APIs |
| Gateway | YARP 2.3 (config-driven routes and clusters), ASP.NET Core rate limiter |
| Database | PostgreSQL 17, one database and one role per service |
| Data access | EF Core 10 + Npgsql (migrations, LINQ), one shared `NpgsqlDataSource` connection pool per service |
| Errors | `Result<T>` / `Error` in code, RFC 9457 problem details at the edge |
| Tests | xUnit v3, Shouldly, `WebApplicationFactory`, Testcontainers (PostgreSQL 17) |
| Tooling | Docker Compose, GitHub Actions, CodeQL, central package management, locked restores |

### 🤔 Why these choices

| Choice | Alternatives considered | Why it fits | When we would switch |
|---|---|---|---|
| **Microservices with a "Transit Core"** | Modular monolith (ADR 0001), one service per module | Identity and Network own different data with different change rates. Money and tickets must change in one transaction, so they stay together | Split Core only if one part needs to scale or deploy on its own, and accept sagas then |
| **YARP gateway** | Ocelot, nginx, a cloud API gateway | .NET-native, configured in `appsettings.json`, and the rate limiter and request id live in C# code we test | A managed gateway when deploying to a cloud with its own edge |
| **Minimal APIs** | MVC controllers | Little ceremony for small services. `MapGroup` keeps routes tidy, and endpoints are plain functions that are easy to test | Controllers if a service grows many filters and conventions |
| **PostgreSQL** | SQL Server, MongoDB | Row locks, CHECK constraints, partial indexes and `SKIP LOCKED`, which the money and gate logic depend on | Not for the core. Read replicas at scale |
| **EF Core + Npgsql** | Dapper, raw ADO.NET | Typed LINQ, migrations per service, parameterized SQL by default. Raw SQL stays available for the locking queries later | Dapper for a hot, hand-tuned read path |
| **Database per service + role per service** | One shared database, schemas only | "Never read another service's tables" is enforced by PostgreSQL (`REVOKE CONNECT`), not by convention | Never, for this design |
| **PBKDF2 (built into .NET)** | bcrypt, Argon2id | No extra dependency, FIPS-approved, OWASP's 600k iterations. The stored format carries its own parameters, so we can upgrade later | Argon2id once a maintained .NET binding is worth the dependency |
| **Precomputed Dijkstra** | Dijkstra per request, Floyd–Warshall, BFS | A small, rarely changing graph. Paying once makes every quote O(1). BFS ignores distance, and Floyd–Warshall is O(V³) | Per-request or incremental routing if the network gets large or live (closures, delays) |
| **Testcontainers** | EF Core in-memory provider, SQLite | The in-memory provider doesn't run SQL or enforce constraints, so it would hide exactly what we test | Not planned |
| **Result pattern** | Exceptions for business errors | Expected failures (not found, conflict, invalid) are values. One mapper turns them into status codes, and every response has a stable `code` | Not planned |

## 📁 Project structure

```
.
├── src/
│   ├── Gateway/TransitPass.Gateway/              YARP routes and clusters (appsettings.json), rate limiting
│   ├── BuildingBlocks/TransitPass.BuildingBlocks/
│   │   ├── Results/                              Result<T>, Error, ErrorType
│   │   ├── Http/                                 Result → status code + RFC 9457 problem
│   │   ├── Pagination/                           opaque cursor, PageRequest (limit+1), CursorPage
│   │   └── Hosting/                              service defaults, request id, Postgres pool + health check
│   └── Services/
│       ├── Identity/TransitPass.Identity.Api/
│       │   ├── Users/                            User, mapping, PhoneNumber, PasswordHasher, register endpoint
│       │   └── Data/                             DbContext, migrations
│       ├── Network/TransitPass.Network.Api/
│       │   ├── Stations/                         Station, mapping, endpoints
│       │   ├── Segments/                         Segment, mapping (track graph table)
│       │   ├── Fares/                            RouteGraph (Dijkstra), IFarePolicy, FareCatalog, endpoint
│       │   └── Data/                             DbContext, migrations
│       └── Core/TransitPass.Core.Api/            wallet, tickets, gate (next phases)
├── tests/                                        one test project per service + building blocks + gateway
├── deploy/
│   ├── postgres/init.sh                          one database + one login role per service
│   └── dev-secrets.sh                            random local passwords into .env (git-ignored)
├── docs/
│   ├── PRD.md                                    product requirements
│   └── adr/                                      19 architecture decision records
├── .github/workflows/ci.yml                      build, format, test, Docker images (+ CodeQL)
├── docker-compose.yml
└── Dockerfile                                    one multi-stage image, used for every service
```

- 🧱 Each feature lives in its own folder: the **entity**, its **EF Core mapping** (`IEntityTypeConfiguration`), and its **endpoints**.
- 🧮 Algorithms are **pure classes** with no database or HTTP (`RouteGraph`, `SlabFarePolicy`, `PhoneNumber`, `PasswordHasher`), so they are unit-tested directly.

## 🔧 Setup

### 📦 Prerequisites
- 🐳 **Docker** with Docker Compose (to run everything, and for the database tests)
- 🟣 **.NET SDK 10** (the version in [`global.json`](global.json)) to develop outside Docker

### 🔐 Environment variables
- 🚫 No password is in the repository. `./deploy/dev-secrets.sh` writes random ones into a git-ignored `.env`.

| Variable | Used by | Meaning |
|---|---|---|
| `POSTGRES_SUPERUSER_PASSWORD` | Compose | PostgreSQL superuser |
| `IDENTITY_DB_PASSWORD`, `NETWORK_DB_PASSWORD`, `CORE_DB_PASSWORD` | Compose, services | One login role per service |
| `POSTGRES_HOST_PORT` | Compose | Host port for PostgreSQL (default `5432`; e.g. `55432` if it's taken) |
| `ConnectionStrings__Default` | each service | Its own database. Outside Docker, set it through user secrets |
| `Database__MigrateOnStartup` | each service | Apply migrations on start (`true` in Development only) |
| `RateLimiting__TokenLimit`, `RateLimiting__TokensPerPeriod` | gateway | Rate-limit bucket size and refill per second |

### 🐳 Run with Docker

```bash
./deploy/dev-secrets.sh            # once
docker compose up --build
curl -i http://localhost:5100/health/live
# Port 5432 already taken? POSTGRES_HOST_PORT=55432 docker compose up --build
```

| Service | URL |
|---|---|
| Gateway (the only public port) | http://localhost:5100 |
| PostgreSQL | `localhost:${POSTGRES_HOST_PORT:-5432}` |

### 💻 Run one service outside Docker

```bash
POSTGRES_HOST_PORT=55432 docker compose up -d postgres         # only the database

dotnet user-secrets --project src/Services/Network/TransitPass.Network.Api init
dotnet user-secrets --project src/Services/Network/TransitPass.Network.Api set ConnectionStrings:Default \
  "Host=localhost;Port=55432;Database=network_db;Username=network_svc;Password=<NETWORK_DB_PASSWORD from .env>"

dotnet run --project src/Services/Network/TransitPass.Network.Api   # :5102, applies migrations in Development
curl "http://localhost:5102/api/v1/network/fares/quote?from=uttara-north&to=motijheel"
```

Local ports: gateway `5100`, identity `5101`, network `5102`, core `5103`.

### 🌱 Migrations and seed data
- 📜 Each service owns its schema through EF Core migrations in `Data/Migrations`. Seed rows (stations, track) live in the migrations, so every environment starts the same.
- 🔄 **Development:** `Database:MigrateOnStartup=true` applies pending migrations when the service starts.
- 🚀 **Production:** it stays off. Migrations run once as a deploy step (`dotnet ef migrations bundle`), so several instances never race to change the schema.

```bash
dotnet tool restore                # the pinned dotnet-ef
dotnet ef migrations add <Name> --project src/Services/Network/TransitPass.Network.Api --output-dir Data/Migrations
```

## 🧪 Tests

```bash
dotnet test        # needs Docker running: database tests start their own PostgreSQL 17
```

- 🐘 Database tests use **Testcontainers**: a throwaway `postgres:17-alpine` per test class, with the real migrations applied. No mocks of the database.
- 🤖 **CI on every pull request:** locked restore, `dotnet format --verify-no-changes`, build with warnings as errors, all tests, Docker image builds, and CodeQL. `main` is protected, so a change merges only when everything is green.

| Requirement | Test |
|---|---|
| 10 sign-ups with one phone at the same instant create exactly one user (1 × 201, 9 × 409) | `tests/TransitPass.Identity.Tests/RegisterEndpointTests.cs` |
| The same phone in another format is refused (`01711…` then `+8801711…` → 409) | `RegisterEndpointTests.cs` |
| The password is never returned or stored in plain text; ids are UUIDv7 | `RegisterEndpointTests.cs` |
| Phone normalization: 6 valid formats, 10 invalid ones (landline, foreign, wrong length, letters, Bengali digits) | `tests/TransitPass.Identity.Tests/UnitTests.cs` |
| PBKDF2: round trip, a different salt every time, malformed hashes never verify | `UnitTests.cs` |
| The database refuses a non-E.164 phone even when the app is bypassed | `RegisterEndpointTests.cs` |
| Dijkstra finds the shortest path (a 2-hop route beats a longer direct track), both directions, unreachable and unknown stations | `tests/TransitPass.Network.Tests/RouteGraphTests.cs` |
| Fare slabs at every boundary (exactly 2 km stays ৳20, 2,001 m is ৳30) | `RouteGraphTests.cs` |
| Fare quotes end to end, and 400 / 404 / 422 problems with their codes | `tests/TransitPass.Network.Tests/FareEndpointTests.cs`, `RouteGraphTests.cs` |
| The database refuses reversed or duplicate track, self-loops, distance ≤ 0, track to an unknown station, and deleting a station with track | `tests/TransitPass.Network.Tests/SegmentSchemaTests.cs` |
| The database refuses non-slug station ids and duplicate names; the id lookup can use the primary key index | `tests/TransitPass.Network.Tests/StationSchemaTests.cs` |
| Station list (sorted) and lookup, 404 problem with `code` and `traceId`, case-sensitive ids | `tests/TransitPass.Network.Tests/StationEndpointTests.cs` |
| The gateway routes each prefix to the right service, and rate-limits with 429 | `tests/TransitPass.Gateway.Tests/GatewayRoutingTests.cs` |
| Result → status code mapping, cursor encoding, request id validation | `tests/TransitPass.BuildingBlocks.Tests/` |

## 🔌 API overview

All paths go through the gateway at `http://localhost:5100`.

| Method | Path | Description |
|---|---|---|
| GET | `/health/live` | `200` when the process is up (each service) |
| GET | `/health/ready` | `200` when the database answers, else `503` (each service) |
| GET | `/api/v1/network/stations` | All stations, sorted by id |
| GET | `/api/v1/network/stations/{id}` | One station, or `404 station.not_found` |
| GET | `/api/v1/network/fares/quote?from=&to=` | `{ from, to, distanceM, farePoisha }` for the shortest track distance |
| POST | `/api/v1/identity/users` | Sign up `{ phone, name, password }` → `201 Created` + `Location`, body `{ id, phone, name, createdAt }` |

```bash
curl -s "localhost:5100/api/v1/network/fares/quote?from=uttara-north&to=motijheel"
# {"from":"uttara-north","to":"motijheel","distanceM":16500,"farePoisha":10000}

curl -s -X POST localhost:5100/api/v1/identity/users -H 'content-type: application/json' \
  -d '{"phone":"01711000001","name":"Rafi","password":"s3cret-pass"}'
# 201 {"id":"019a…","phone":"+8801711000001","name":"Rafi","createdAt":"…"}
```

### ❗ Errors

- 🧩 Code never builds HTTP responses by hand. A business failure is an `Error(code, message, type)`, and one mapper turns it into a problem response:

```json
{ "type": "https://transitpass.dev/errors/station.not_found", "title": "Station 'dhanmondi' does not exist.",
  "status": 404, "code": "station.not_found", "traceId": "00-…" }
```

| Status | When | Codes so far |
|---|---|---|
| `400` | The request is invalid | `fare.invalid_request`, `fare.same_station`, `user.invalid_phone`, `user.invalid_name`, `user.invalid_password` |
| `404` | It doesn't exist | `station.not_found` |
| `409` | It conflicts with what's already there | `user.phone_taken` |
| `422` | Valid request, but a business rule says no | `fare.no_route` |
| `429` | Too many requests from one client | (gateway rate limit, with `Retry-After`) |
| `503` | The database is unreachable | (`/health/ready`) |

## 💰 Fare model

- 🛤️ **Distance is the shortest track distance** between the two stations, found by Dijkstra over the `segments` graph.
- 🧾 **Price is by slab:**

| Distance | Fare |
|---|---:|
| up to 2 km | ৳20 |
| up to 5 km | ৳30 |
| up to 8 km | ৳40 |
| up to 12 km | ৳60 |
| up to 16 km | ৳80 |
| longer | ৳100 |

- 📏 Exactly on a limit stays in that slab (2,000 m → ৳20; 2,001 m → ৳30).
- 🔁 The same price both ways (the graph is undirected).
- 💵 Money is integer **poisha** everywhere: ৳100 is `10000`.
- 🧩 Pricing sits behind `IFarePolicy` (Strategy pattern), so a student or off-peak policy can replace it without touching routing.

### 🧭 The track graph (seed data)

```
 uttara-north ──9.0 km── agargaon ──4.0 km── karwan-bazar ──3.5 km── motijheel
```

| From → To | Distance | Fare |
|---|---:|---:|
| Agargaon → Karwan Bazar | 4.0 km | ৳30 |
| Uttara North → Agargaon | 9.0 km | ৳60 |
| Uttara North → Motijheel | 16.5 km | ৳100 |

### ⚙️ How a quote is computed

| Step | Data structure / algorithm | Cost |
|---|---|---|
| Load stations and segments (once) | two `AsNoTracking` queries | O(V + E) |
| Build the graph | adjacency list, stations numbered 0..V−1 | O(V + E) |
| All shortest distances | Dijkstra × V with a binary heap (`PriorityQueue`), lazy deletion | O(V · (V + E) log V) time, O(V²) memory |
| Each quote: distance | dictionary + flat V×V array | **O(1)** |
| Each quote: price | `Array.BinarySearch` over slab limits | O(log s) |

- 🔒 The graph is built **once**, behind a `SemaphoreSlim`. Concurrent first requests build it one time, and a failed build (database down) is not cached, so the next request retries.

## 🔒 Concurrency

### Built: one phone, one account
- 🎯 **The case:** two sign-ups with the same phone arrive at the same instant.
- ❌ **The naive way** checks "is this phone taken?" with a `SELECT`, then `INSERT`s. Both requests see "free", and both insert.
- ✅ **TransitPass inserts directly.** The `ux_users_phone` unique index decides inside PostgreSQL: the second insert waits for the first transaction, then fails with `23505`, which becomes `409 user.phone_taken`.
- 🧪 **Proof:** 10 parallel sign-ups with one phone → exactly one `201`, nine `409`, one row.

### Designed (next phases): Rafi's last ৳100
- 🎯 Two purchases of a ৳100 ticket with ৳100 in the wallet, at the same instant.
- 🛡️ A single conditional update, `UPDATE wallets SET balance = balance − fare WHERE id = … AND balance >= fare`, plus `CHECK (balance >= 0)`. The check and the subtraction are one statement, so only one purchase can win.
- 🔐 Gate taps lock rows in one fixed order (ticket → journey → account), so two gates can't deadlock. Background jobs use `FOR UPDATE SKIP LOCKED`, so several workers never take the same row.
- 🧪 It will be proved the same way: 200 parallel purchases against real PostgreSQL.

## 🛡️ Security basics

- 🔑 **Passwords:** PBKDF2-HMAC-SHA256, 600,000 iterations, a 16-byte random salt per user, and `FixedTimeEquals` for comparison. Never returned by the API, never logged. Limited to 8–128 characters, because PBKDF2 cost grows with the input and an unbounded password is a cheap way to slow the server down.
- 🗄️ **Least privilege in the database:** each service connects as its own role and owns only its database. `REVOKE CONNECT … FROM PUBLIC` means `identity_svc` gets "permission denied" on `core_db`.
- 🧱 **The database is the last line:** CHECK constraints (E.164 phone, slug ids, positive distances, canonical track order), UNIQUE indexes and FOREIGN KEYs hold even if application code has a bug, or someone writes SQL directly.
- 💉 **No SQL injection:** every query goes through EF Core with parameters (`WHERE s.id = @id`). User input never becomes SQL text.
- 🚦 **Rate limit** at the gateway per client IP, with `429` and `Retry-After`.
- 🪪 **Request id:** `X-Request-Id` is accepted only as a UUID and re-printed from the parsed value, so a client can't inject fake log lines (found by CodeQL, fixed in code).
- 🔒 **Secrets:** none in the repository. Local passwords are random, in a git-ignored `.env`.
- 🐳 **Containers** run as a non-root user, and restores are locked (`packages.lock.json`), so a build can't pull a different dependency than the one reviewed.
- 🔍 **CodeQL** scans every pull request, and findings are fixed in code, not dismissed.
- 🔜 **Next (Phase 5–6):** ES256 JWT signed only by Identity, a public key published at `/.well-known/jwks.json` with a `kid` for rotation, and verification in each service with the public key only.

## 📌 Key decisions and trade-offs

Each one has a full record in [`docs/adr`](docs/adr/README.md).

| Decision | Trade-off we accepted |
|---|---|
| Microservices with wallet, tickets and gates kept together (ADR 0019) | Four processes to run, and no cross-service joins. In return, money and tickets change in one ACID transaction, without sagas |
| Database per service, no foreign keys across services | Integrity across services comes from verified tokens and snapshots, not the database |
| Integer poisha and a double-entry, append-only ledger (ADR 0005) | More rows than a single balance column. In return every taka has a history, and refunds are reversals, never edits |
| Insert-and-catch on UNIQUE instead of check-then-insert | Relies on reading PostgreSQL error codes (`23505`). In return it is correct under any concurrency |
| Phone normalized to E.164 and checked again by the database | Only Bangladeshi mobile numbers for now |
| Track stored once per stretch, in canonical order | Code must sort the two ids before writing. In return the two directions can never disagree |
| All-pairs distances precomputed in memory | A track change needs a restart. Fine for data that changes a few times a year |
| No auto-migration in production | A separate deploy step. In return, replicas never race to change the schema |
| Real PostgreSQL in tests (Testcontainers) | Tests need Docker and take longer. In return constraints, SQL and races are tested for real |
| Every change through a PR with CI and CodeQL, squash-merged into a protected `main` | Slower than pushing directly. In return every step has a reviewed record |
| No blockchain | Considered and rejected: the threats are forgery, replay and races, which signatures, a server-side state machine, row locks and an append-only ledger solve at a fraction of the cost |

## 🚧 Known limitations

- 🔐 **No login yet.** Users can sign up but not sign in, so no endpoint is protected yet (Phases 5–6).
- 💳 **No wallet, tickets or gates yet.** Transit Core is wired to its database but has no features yet.
- 📱 **Bangladeshi mobile numbers only,** and phones are not verified (no OTP yet).
- 🗺️ **Seed network is four stations.** Station and track changes are made through migrations, and the fare graph picks them up on restart.
- 🧮 **One fare policy.** No student, off-peak or capped-day fares yet (the `IFarePolicy` seam is there).
- 📉 **No load test yet.** Correctness under races is tested, throughput is not.
- 🖥️ **No frontend yet.** Use `curl`, or the OpenAPI document each service exposes in Development (`/openapi/v1.json`).

## 🛣️ Roadmap

Each phase is one small pull request with its own tests.

| # | Phase | Status |
|---|---|---|
| 0 | Gateway, three services, database per service, Docker, CI | ✅ #1–#3 |
| 1 | Station list and lookup | ✅ #4 |
| 2 | Stations in PostgreSQL with EF Core | ✅ #7 |
| 3 | Track graph and fare quotes (Dijkstra) | ✅ #8 |
| 4 | Sign-up | ✅ #9 |
| 5 | Login: ES256 JWT and a JWKS endpoint | ⏳ next |
| 6 | Protected endpoints across services (401 / 403) | |
| 7 | Wallet and double-entry ledger, history with cursor pagination | |
| 8 | Top-up through an HMAC-signed payment webhook, an inbox for duplicates | |
| 9 | Ticket purchase: `Idempotency-Key`, atomic debit, a 200-way concurrency test | |
| 10 | Ticket list and cancel with refund (ledger reversal) | |
| 11 | Signed QR credential (JWS) | |
| 12 | Gate entry and exit: a journey state machine, row locks in a fixed order | |
| 13 | Background refunds for expired tickets (`SKIP LOCKED`, a partial index) | |
| 14 | Demo script: sign up → top up → buy → tap in → tap out | |

**After that:** refresh token rotation, phone OTP, Redis for rate limits and replay checks, a rotating device-bound QR code, an offline gate agent (SQLite outbox), a Next.js app and OpenTelemetry tracing.

## 🧭 How it's built

- 🌿 **One phase = one branch = one pull request.** Branches are `feat/…`, `fix/…` or `docs/…`, and nothing is pushed to `main` directly.
- ✅ **CI must be green** (format, build, tests, Docker images, CodeQL) before a pull request merges. Merges are squashed, so `main` reads as one commit per feature.
- 📝 **Every pull request says** what was built, how, which alternatives were rejected and why, the edge cases, the test evidence, and a data-structure and complexity table.
- 📚 **Every significant decision** gets an ADR. When a decision changes, a new ADR supersedes the old one (ADR 0019 superseded ADR 0001).
- 🐘 **The database is designed first:** requirements → entities → relations → normalization → constraints → indexes from the real queries. Each table arrives with the phase that needs it.

---

<p align="center"><sub>MIT licensed · built by <a href="https://github.com/istiak133">Istiak Ahmed</a></sub></p>
