# Contributing

## Workflow
1. Branch from `main`: `feat/<scope>`, `fix/<scope>`, `chore/<scope>`, `docs/<scope>`.
2. Commit with [Conventional Commits](https://www.conventionalcommits.org/): `feat(wallet): add atomic debit`.
3. Open a PR using the template. Every section is filled in or explicitly marked N/A.
4. CI must be green. PRs are squash-merged; the PR title becomes the commit message.

## Local checks (same as CI)
```bash
dotnet restore --locked-mode
dotnet format --verify-no-changes
dotnet build -c Release
dotnet test -c Release
```

## Rules of the road
- Time comes from `TimeProvider`, never `DateTime.UtcNow`.
- Money is the `Money` value object over `long` minor units (poisha), stored as `bigint` ([ADR 0005](docs/adr/0005-money-and-double-entry-ledger.md)).
- Primary keys are UUIDv7; tables are `snake_case` in a per-module schema ([ADR 0015](docs/adr/0015-data-conventions.md)).
- A non-obvious technical decision gets an ADR in `docs/adr/`.
