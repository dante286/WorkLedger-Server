# WorkLedger Server

Backend for WorkLedger — C# / ASP.NET Core Minimal APIs on .NET 10, EF Core + Npgsql → PostgreSQL (from S1
onward), JWT bearer auth (from S2), xUnit + Testcontainers, Docker Compose.

This is a separate repository from `WorkLedger-Client`. The two meet only at `WorkLedger.Contracts` — see
`plans/README.md` for the cross-repo contract handoff and the full build-plan sequence (S0–S5). This README
covers only what S0 built: the solution scaffold, the `/health` endpoint, and local dev setup. It will expand
as later plans land.

## Stack

- **src/WorkLedger.Server** — ASP.NET Core Minimal API (`net10.0`).
- **src/WorkLedger.Contracts** — dependency-free class library with the sync wire DTOs and entity payload
  DTOs shared with the client. No ASP.NET/EF references, so the client can reference it later without
  pulling in server-only packages.
- **tests/WorkLedger.Server.Tests** — xUnit, using `WebApplicationFactory<Program>` for integration tests.

## Running locally

1. Start Postgres (dev-only, no API container yet):

   ```
   docker compose -f docker-compose.dev.yml up -d
   ```

   This brings up Postgres on `localhost:55432` (non-default port, to avoid clashing with any existing local
   Postgres). Connection string for local dev:

   ```
   Host=localhost;Port=55432;Database=workledger;Username=workledger;Password=workledger
   ```

   Nothing in `src/WorkLedger.Server` talks to this database yet — that starts in S1. This compose file exists
   purely so `dotnet run` has a real Postgres available during development.

2. Run the API:

   ```
   dotnet run --project src/WorkLedger.Server
   ```

   Then check:
   - `GET http://localhost:<port>/health` → `{ "status": "ok", "utc": "<UTC timestamp>" }`
   - `GET http://localhost:<port>/` → `{ "name": "WorkLedger.Server", "version": "..." }`

## Testing

```
dotnet test WorkLedger-Server.slnx
```

Runs the health-check integration test (and everything added in later plans). Tests do not depend on
`docker-compose.dev.yml` — from S1 onward they use Testcontainers to spin up their own disposable Postgres.

## CI

`.github/workflows/build.yml` restores, builds (`Release`), and runs `dotnet test` on `ubuntu-latest` for every
push and pull request.

## Contracts publishing

Not yet set up. `WorkLedger.Contracts` is authored here and will be consumed by the client once its real
`ApiSyncProvider` is implemented (client Plan 6). Publishing to GitHub Packages, or a git submodule, is a
follow-up — deferred per plan S0 since the client doesn't consume it yet.

## Notes for S1+

The DTOs in `src/WorkLedger.Contracts/Payloads/EntityPayloads.cs` are **placeholders** copied from the plan
skeleton, not yet reconciled against the client's `Models/Project.cs`, `Models/TaskItem.cs`, and
`Models/TimeEntry.cs`. Confirm the exact field lists (and whether `TimeEntry` stores a computed duration vs.
start/end) before S1/S3 depend on them.
