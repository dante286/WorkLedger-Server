# WorkLedger Server

Backend for WorkLedger — C# / ASP.NET Core Minimal APIs on .NET 10, EF Core + Npgsql → PostgreSQL, JWT bearer
auth (from S2), xUnit + Testcontainers, Docker Compose.

This is a separate repository from `WorkLedger-Client`. The two meet only at `WorkLedger.Contracts` — see
`plans/README.md` for the cross-repo contract handoff and the full build-plan sequence (S0–S5). This README
covers what S0–S4 built: the solution scaffold and `/health`, the EF Core data model and migrations, JWT
device bootstrap, the `/sync` push/pull endpoints, and the `/reports` rollups. Deployment (S5) is still to
come.

## Stack

- **src/WorkLedger.Server** — ASP.NET Core Minimal API (`net10.0`), EF Core + Npgsql. See
  `src/WorkLedger.Server/Data/README.md` for the data layer and `dotnet ef` commands.
- **src/WorkLedger.Contracts** — dependency-free class library with the sync wire DTOs and entity payload
  DTOs shared with the client. No ASP.NET/EF references, so the client can reference it later without
  pulling in server-only packages.
- **tests/WorkLedger.Server.Tests** — xUnit; `WebApplicationFactory<Program>` for the `/health` integration
  test, `Testcontainers.PostgreSql` for the data-layer tests.

## Running locally

1. Start Postgres (dev-only, no API container yet):

   ```
   docker compose -f docker-compose.dev.yml up -d
   ```

   This brings up Postgres on `localhost:15432` (non-default port, to avoid clashing with any existing local
   Postgres; 5433 is reserved for a future TLS listener). Connection string for local dev:

   ```
   Host=localhost;Port=15432;Database=workledger;Username=workledger;Password=workledger
   ```

2. Set the auth secrets (never hardcoded — the app fails fast at startup if these are missing):

   ```
   export Jwt__SigningKey="$(openssl rand -base64 48)"
   export Bootstrap__Secret="$(openssl rand -base64 32)"
   ```

   `Account__Name` is optional (defaults to `Default`) — it names the single v1 account seeded on startup.

3. Run the API:

   ```
   dotnet run --project src/WorkLedger.Server
   ```

   On startup the app runs `Database.Migrate()` against the connection string above (see
   `Database:MigrateOnStartup` in `appsettings.json`), so Postgres from step 1 must already be up and reachable —
   the app will fail to start otherwise. See `src/WorkLedger.Server/Data/README.md` for the tradeoffs of
   migrating on boot. It also seeds the single v1 `Account` row (see S2) if one doesn't already exist.

   Then check:
   - `GET http://localhost:<port>/health` → `{ "status": "ok", "utc": "<UTC timestamp>" }`
   - `GET http://localhost:<port>/` → `{ "name": "WorkLedger.Server", "version": "..." }`
   - `POST http://localhost:<port>/auth/register-device` with header `X-Bootstrap-Secret: <Bootstrap__Secret>`
     and body `{ "deviceId": "<any GUID string>" }` → `{ "token": "<JWT>", "expiresAtUtc": "..." }`
   - `GET http://localhost:<port>/auth/whoami` with header `Authorization: Bearer <token>` →
     `{ "accountId": 1, "deviceId": "<the GUID you sent>" }`. Every other endpoint requires that same
     `Authorization` header (401 without it).

## Reports

Three read-only, account-scoped endpoints (S4). They are the **authoritative cross-device rollup**: once
several devices sync into one account, only the server holds the merged picture. The client's own Reports tab
computes its local view separately and does not consume these.

```
GET /reports/weekly?weekOf=<yyyy-MM-dd>
GET /reports/projects/{projectSyncId}?from=<yyyy-MM-dd>&to=<yyyy-MM-dd>
GET /reports/tasks/{taskSyncId}?from=<yyyy-MM-dd>&to=<yyyy-MM-dd>
```

All three take `Authorization: Bearer <token>` and report on **that token's account** — nothing in a URL can
widen the scope. Ids in routes are cross-device **SyncIds** (Guid), never the server's internal int Ids. An
unknown or deleted project/task is a 404, which is also the answer for one belonging to another account.

**Parameters.** `weekOf` names a week, not a start date: any day in the ISO week resolves to that
Monday–Sunday, and omitting it means the current UTC week. `from`/`to` are **inclusive whole UTC days**, so
`from=2026-03-02&to=2026-03-02` is that entire day; either may be omitted and the default is all-time.
`from > to` is a 400, not an empty report. Everything is UTC; the caller localizes.

**What counts.** A time entry contributes only if it, its task, and that task's project are all alive
(tombstoned rows are excluded along the whole chain) and it has an `EndTime`. Its length is the stored
`DurationSeconds`, falling back to `EndTime − StartTime`. It lands wholly in the UTC day it *started*, so an
entry spanning midnight is not split — the same rule the client's own rollup uses. Entries still running have
no fixed length and are excluded from every total, but each report carries a `runningEntries` count so a
caller can say "plus one timer still going" instead of silently under-reporting.

The weekly report's `days` array always has seven entries, zero-filled, so it can be charted directly; its
account total, per-project, per-task and per-day views are all folded from one aggregation and cannot
disagree.

**CSV export.** Add `?format=csv` (or send `Accept: text/csv` — the query parameter wins, and a wildcard
`*/*` stays JSON) to get an RFC 4180 download instead. An unrecognized `format` is a 400. Columns:

| Endpoint | Columns |
|---|---|
| `/reports/weekly` | `date,project,task,seconds` |
| `/reports/projects/{id}` | `project,task,seconds` |
| `/reports/tasks/{id}` | `date,project,task,seconds` |

The weekly file is the (day × project × task) grid the JSON's three breakdowns are each folded from, so every
one of them is a single pivot away in a spreadsheet. Days with no time have no row, and `runningEntries` is
JSON-only — a header-level scalar has no honest column in a table of recorded time.

Response DTOs live in `src/WorkLedger.Contracts/Reports/ReportDtos.cs`; the counting rules are stated once, in
full, on `ReportService`.

## Testing

```
dotnet test WorkLedger-Server.slnx
```

Runs the `/health` integration test, the EF Core data-layer tests, the auth tests (device bootstrap, JWT
issuance/validation, 401s), the sync reconciliation tests, and the report tests (hand-computed weekly totals,
tombstone exclusion, date-range clipping, CSV columns, cross-account isolation). Tests do not depend on
`docker-compose.dev.yml` or any real secrets — the
data-layer and auth tests each spin up their own disposable Postgres via Testcontainers, the auth tests supply
throwaway `Jwt:SigningKey`/`Bootstrap:Secret` values via `WebApplicationFactory` config overrides (see
`tests/WorkLedger.Server.Tests/TestConfig.cs`), and the `/health` test disables `Database:MigrateOnStartup` so
it doesn't need a database at all. Testcontainers needs a working Docker daemon (present by default on GitHub's
`ubuntu-latest` runners).

## CI

`.github/workflows/build.yml` restores, builds (`Release`), and runs `dotnet test` on `ubuntu-latest` for every
push and pull request.

## Contracts publishing

Not yet set up. `WorkLedger.Contracts` is authored here and will be consumed by the client once its real
`ApiSyncProvider` is implemented (client Plan 6). Publishing to GitHub Packages, or a git submodule, is a
follow-up — deferred per plan S0 since the client doesn't consume it yet.

## Contract reconciliation

The DTOs in `src/WorkLedger.Contracts/Payloads/EntityPayloads.cs` are reconciled against the client's
`Models/Project.cs`, `Models/TaskItem.cs`, and `Models/TimeEntry.cs` (checked 2026-08-11, client at
`E:\GitHub-Repos\WorkLedger-Client`). Notably: `Project` has no `IsArchived`; `TaskItem` has `Notes` and
`IsCompleted` in addition to `IsArchived`; `TimeEntry` stores both `StartTime`/`EndTime` **and** a persisted
`DurationSeconds` (not computed-only). Parent links (`ProjectSyncId`, `TaskSyncId`) resolve by `SyncId`, never
the client's local autoincrement `Id`/`ProjectId`/`TaskItemId`.
