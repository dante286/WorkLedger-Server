# WorkLedger Server

The backend for [WorkLedger](https://github.com/dante286/WorkLedger-Client): it holds the authoritative
copy of an account's projects, tasks and time entries, reconciles the changes each device pushes, and
computes the cross-device rollups no single device can. Self-hosted by design — one `docker compose up`
on a homelab box or a small VPS, with your data in a Postgres volume you own and back up.

C# / ASP.NET Core Minimal APIs on .NET 10, EF Core + Npgsql → PostgreSQL, JWT bearer auth, xUnit +
Testcontainers.

- **Deploying it** → [Quickstart](#quickstart-self-hosted-with-docker-compose), then
  [Configuration](#configuration) and [Operations](#operations).
- **Working on it** → [Development](#development). The design record is `plans/` (S0–S5) and
  `plans/README.md`.

## Architecture

Three projects (`WorkLedger-Server.slnx`), no controllers — everything is wired in `Program.cs`:

| Project | What it is |
|---|---|
| `src/WorkLedger.Server` | The API. Minimal API endpoints, EF Core + Npgsql, JWT bearer auth, `Sync/` (push/pull reconciliation) and `Reports/` (read-only rollups). |
| `src/WorkLedger.Contracts` | Dependency-free class library: the wire DTOs. No ASP.NET or EF references, because the client compiles these same files (see [Cross-repo](#cross-repo-the-contract-is-vendored)). |
| `tests/WorkLedger.Server.Tests` | xUnit. `WebApplicationFactory<Program>` plus `Testcontainers.PostgreSql`, so the data, auth, sync and report tests each run against a real throwaway Postgres. |

Two properties shape everything else:

- **Every endpoint is authenticated by default.** `Program.cs` installs an authorization
  `FallbackPolicy` requiring an authenticated user, so an endpoint is protected unless it explicitly
  opts out with `.AllowAnonymous()` — only `/`, `/health` and `/auth/register-device` do. Adding an
  endpoint cannot accidentally leave it open.
- **Scope always comes from the token.** `ICurrentContext` reads `account_id`/`device_id` out of the
  JWT claims, and every sync and report query filters on that account. Nothing in a request body or URL
  can widen it. Ids in routes and payloads are cross-device `SyncId` Guids, never the server's internal
  int ids, so an id belonging to another account is simply a 404.

v1 is single-account / multi-device: one `Account` row (seeded on first boot) and one `Device` row per
registered device.

## Quickstart: self-hosted with Docker Compose

Requires Docker with Compose v2. From a clean checkout:

```bash
cp .env.example .env

# Fill in the three required secrets. On Linux/macOS:
#   Jwt__SigningKey    — at least 32 bytes; signs device tokens
#   Bootstrap__Secret  — gates device registration
#   POSTGRES_PASSWORD  — the bundled database's password (set it before the first boot)
openssl rand -base64 48   # once per secret
$EDITOR .env

docker compose up -d
```

That builds the API image, starts Postgres, waits for it to pass its healthcheck, then starts the API —
which applies the EF migrations and seeds the single account before it serves anything. A fresh host
needs no other provisioning step.

```bash
curl http://localhost:8080/health      # {"status":"ok","utc":"..."}
curl http://localhost:8080/            # {"name":"WorkLedger.Server","version":"..."}
docker compose ps                      # both services should read "healthy"
```

If the API container exits instead, `docker compose logs api` names the configuration key it is missing
— it refuses to start on a missing signing key, bootstrap secret or database rather than serving
half-configured. Compose itself refuses the `up` if a required variable is absent from `.env`.

### Registering a device

Devices enrol once, with the bootstrap secret, and keep a bearer token:

```bash
curl -X POST http://localhost:8080/auth/register-device \
  -H "X-Bootstrap-Secret: $Bootstrap__Secret" \
  -H 'Content-Type: application/json' \
  -d '{"deviceId":"laptop-01","label":"Work laptop"}'
# {"token":"eyJ...","expiresAtUtc":"2027-..."}

curl http://localhost:8080/auth/whoami -H "Authorization: Bearer $token"
# {"accountId":1,"deviceId":"laptop-01"}
```

`deviceId` is the client's own stable device identifier; re-registering the same one refreshes its token
and label rather than creating a second device. Every other endpoint wants that
`Authorization: Bearer <token>` header and returns 401 without it. Tokens last `Jwt__ExpiryDays` (365 by
default) and there is no refresh or revocation in v1 — see
[Security posture](#security-posture-and-deferred-hardening).

## Endpoints

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /` | none | Name and running version (the image's `VERSION` build arg). |
| `GET /health` | none | Liveness. Touches no database, which is why it is also the container's `HEALTHCHECK`. |
| `POST /auth/register-device` | `X-Bootstrap-Secret` header | Enrol a device; returns a JWT and its expiry. |
| `GET /auth/whoami` | bearer | The account and device the caller's token resolves to. |
| `POST /sync/push` | bearer | Submit a batch of create/update/delete changes; returns one result per change, in request order. |
| `GET /sync/pull?since=<iso-8601>` | bearer | Changes from other devices with `UpdatedAt > since`. Omit `since` for everything. |
| `GET /reports/weekly?weekOf=<yyyy-MM-dd>` | bearer | One ISO week (Monday–Sunday, UTC), account-wide. |
| `GET /reports/projects/{projectSyncId}?from=&to=` | bearer | One project over an optional inclusive UTC date range. |
| `GET /reports/tasks/{taskSyncId}?from=&to=` | bearer | One task, with a per-day breakdown. |

All three report endpoints also take `?format=csv` (see [Reports](#reports)).

## Configuration

Everything is environment-driven; `docker-compose.yml` reads it from `.env` (start from `.env.example`,
which is the annotated list). Names use ASP.NET Core's `__` section separator, so `Jwt__SigningKey` is
the `Jwt:SigningKey` key.

**Required** — the app fails fast at startup without them:

| Variable | Notes |
|---|---|
| `Jwt__SigningKey` | Signs device tokens. At least 32 bytes. Changing it invalidates every issued token, so every device must register again. |
| `Bootstrap__Secret` | Gates `POST /auth/register-device`. Treat it like a password; rotate it once your devices are enrolled. |
| `POSTGRES_PASSWORD` | Password for the bundled Postgres. Baked into the volume by `initdb` on the first boot — changing it later needs an `ALTER USER`, not an edit here. |

A database must be reachable one of two ways: the four `POSTGRES_HOST` / `POSTGRES_DB` /
`POSTGRES_USER` / `POSTGRES_PASSWORD` variables (what compose passes, so the password lives in exactly
one place), or a full `ConnectionStrings__Postgres` DSN, which wins when set and is how you point at an
external or managed Postgres. A blank value counts as unset. The resolution and its failure message live
in `src/WorkLedger.Server/Data/PostgresConnection.cs`.

**Optional:**

| Variable | Default | Notes |
|---|---|---|
| `POSTGRES_DB` / `POSTGRES_USER` | `workledger` | Database and role for the bundled Postgres. |
| `Jwt__ExpiryDays` | `365` | Device token lifetime. |
| `Jwt__Issuer` / `Jwt__Audience` | `WorkLedger.Server` / `WorkLedger.Client` | Validated on every request; change both sides together. |
| `Account__Name` | `Default` | Names the single v1 account seeded on first boot. Cosmetic. |
| `Database__MigrateOnStartup` | `true` | Set `false` to apply migrations as a separate step instead (see [Schema changes](#schema-changes)). |
| `API_PORT` / `API_BIND` | `8080` / `0.0.0.0` | Host side of the published port. `API_BIND=127.0.0.1` when a reverse proxy is the only thing that should reach it. |
| `WORKLEDGER_VERSION` | `latest` | Image tag to run, and the version stamped into a local build. |
| `ASPNETCORE_ENVIRONMENT` | `Production` (set in the image) | `Development` turns on the developer exception page — do not use it on a deployed instance. |

## Sync

`POST /sync/push` and `GET /sync/pull` implement last-write-wins reconciliation over the shared
`WorkLedger.Contracts` DTOs. The rules are stated once, in full, in the XML doc comment on
`SyncService` (rules A–F) and mirrored by the client and by `plans/S3-sync-endpoints.md`; read that
comment before changing merge behaviour. In outline:

- Higher `Version` wins. A losing push is answered with the server's own version and payload, so the
  pusher can adopt the winner without waiting for its next pull.
- Deletes are accepted at `>=` and stored one version higher, so a tombstone strictly beats every
  peer's copy (a deliberate deviation from strict LWW, forced by the client — WL-93).
- Replays are accepted as no-ops with no database write at all: `UpdatedAt` is the pull cursor, so an
  incidental write would re-broadcast the row to every other device.
- Results are **positional** — one per submitted change, in request order — because one batch can carry
  create → update → delete for the same entity. A batch is applied parents-first (project → task → time
  entry) inside one transaction, but answered in the order it arrived.
- A pull never returns the calling device its own writes.

## Reports

Three read-only, account-scoped endpoints. They are the **authoritative cross-device rollup**: once
several devices sync into one account, only the server holds the merged picture. (The client's Reports
tab computes its own local view and does not consume these.)

```
GET /reports/weekly?weekOf=<yyyy-MM-dd>
GET /reports/projects/{projectSyncId}?from=<yyyy-MM-dd>&to=<yyyy-MM-dd>
GET /reports/tasks/{taskSyncId}?from=<yyyy-MM-dd>&to=<yyyy-MM-dd>
```

**Parameters.** `weekOf` names a week, not a start date: any day in the ISO week resolves to that
Monday–Sunday, and omitting it means the current UTC week. `from`/`to` are **inclusive whole UTC days**,
so `from=2026-03-02&to=2026-03-02` is that entire day; either may be omitted, and the default is
all-time. `from > to` is a 400, not an empty report. Everything is UTC; the caller localizes. An unknown
or deleted project/task is a 404 — which is also the answer for one belonging to another account.

**What counts.** A time entry contributes only if it, its task, and that task's project are all alive
(tombstoned rows are excluded along the whole chain) and it has an `EndTime`. Its length is the stored
`DurationSeconds`, falling back to `EndTime − StartTime`. It lands wholly in the UTC day it *started*, so
an entry spanning midnight is not split — the same rule the client's own rollup uses. Entries still
running have no fixed length and are excluded from every total, but each report carries a
`runningEntries` count, so a caller can say "plus one timer still going" instead of silently
under-reporting. The counting rules are stated in full on `ReportService` (rules 1–6), with
`ReportQuery` as their SQL form.

The weekly report's `days` array always has seven entries, zero-filled, so it can be charted directly;
its account total, per-project, per-task and per-day views are all folded from one aggregation and
cannot disagree.

**CSV export.** Add `?format=csv` (or send `Accept: text/csv` — the query parameter wins, and a wildcard
`*/*` stays JSON) to get an RFC 4180 download instead. An unrecognized `format` is a 400.

| Endpoint | Columns |
|---|---|
| `/reports/weekly` | `date,project,task,seconds` |
| `/reports/projects/{id}` | `project,task,seconds` |
| `/reports/tasks/{id}` | `date,project,task,seconds` |

The weekly file is the (day × project × task) grid the JSON's three breakdowns are each folded from, so
every one of them is a single pivot away in a spreadsheet. Days with no time have no row, and
`runningEntries` is JSON-only — a header-level scalar has no honest column in a table of recorded time.

Response DTOs live in `src/WorkLedger.Contracts/Reports/ReportDtos.cs`.

## Operations

### Backups

The named volume `workledger_workledger-postgres-data` **is** the deployment. Back up its contents, not
the container:

```bash
# Logical dump (portable across Postgres versions; restore with pg_restore)
docker compose exec -T postgres pg_dump -U workledger -Fc workledger > workledger-$(date +%F).dump

# Restore into an empty database
docker compose exec -T postgres pg_restore -U workledger -d workledger --clean --if-exists < workledger-2026-08-19.dump
```

A dump is a plain file: keep it somewhere off the host, and check it restores at least once. Two things
worth knowing before an emergency:

- Devices hold no unsynced authority once they have pushed, but they *do* hold local data the server has
  never seen, so a restore of an older dump plus a client push converges rather than losing that work —
  the higher `Version` still wins.
- The JWT signing key is **not** in the database. Losing `.env` means every device has to register
  again, so back it up (encrypted) alongside the dumps.

### Upgrading

```bash
git pull                                  # or just edit WORKLEDGER_VERSION in .env
docker compose pull                       # if running a published GHCR tag
docker compose up -d                      # recreates the api container
```

Migrations are applied by the API on boot, so an upgrade that adds a column needs no separate step. Take
a dump first anyway: a migration is the one kind of change that a rollback of the image cannot undo.

### Putting it behind TLS

The API speaks plain HTTP inside the compose network and publishes one port; it does not terminate TLS.
For anything reachable from outside a trusted LAN, put a reverse proxy in front and let it hold the
certificate. Bind the API to localhost first, so the proxy is the only route in:

```dotenv
# .env
API_BIND=127.0.0.1
```

Then point NGINX (or Caddy, or Traefik) at `127.0.0.1:8080`, forwarding `Host`, `X-Forwarded-For` and
`X-Forwarded-Proto`. Nothing in the app depends on the request scheme — auth is a bearer header, there
are no cookies and no redirect-to-HTTPS — so no `ForwardedHeaders` configuration is required for it to
work correctly; add it only if you want client IPs in the logs. If the proxy runs as its own compose
service instead, drop the `ports:` mapping from `api` altogether and let the proxy reach it by service
name on the shared network.

### Logs

`docker compose logs -f api`. One line per request — method, path, status — plus EF Core's migration
output on boot. Deliberately narrow: `Request.Path` excludes the query string and no header is logged,
which is what keeps bearer tokens and the bootstrap secret out of the log, since every secret this API
accepts arrives in a header. EF's SQL logging is set to `Warning` in `appsettings.json` so a production
log is not a transcript of every query.

Three lines are normal and not worth chasing:

- On the **very first** boot, a `fail: Microsoft.EntityFrameworkCore.Database.Command` for
  `SELECT ... FROM "__EFMigrationsHistory"` — EF looking for a migrations table that does not exist yet,
  immediately before it creates it.
- Two `Microsoft.AspNetCore.DataProtection` warnings about keys not being persisted outside the
  container. Nothing here uses data protection — the JWTs are signed with `Jwt__SigningKey`, which is
  yours and stable — so an ephemeral key ring is harmless.

### Smoke test

`scripts/smoke-test.ps1` drives a **running deployment** the way two real devices would: register two
devices, push a project → task → two time entries from one, pull them on the other, edit from the second
and pull the edit back on the first, push a stale edit and check the server hands back the winner, then
check every report agrees with what was pushed (including the CSV header and the 404/400 paths).

```powershell
pwsh ./scripts/smoke-test.ps1              # against a stack that is already up
pwsh ./scripts/smoke-test.ps1 -Up          # docker compose up --build first
pwsh ./scripts/smoke-test.ps1 -BaseUrl https://workledger.example.com -BootstrapSecret <secret>
```

It reads `API_PORT` and `Bootstrap__Secret` from `.env` when not given, asserts every step, and exits
non-zero on the first failure. This is not a substitute for `dotnet test` — the merge and counting rules
are covered there. What only the smoke test catches is the deployment: the image, the container
configuration, migrations-on-boot, and a token minted by the container being accepted by it. It writes
real rows (one project named `Smoke test <timestamp>`) and does not clean up, so point it at a
deployment you don't mind seeding, or drop the volume afterwards with `docker compose down -v`.

## Security posture and deferred hardening

What v1 does:

- Nothing is anonymous except `/`, `/health` and device registration, and that is enforced by an
  authorization fallback policy rather than per-endpoint attributes.
- Every query is scoped to the token's account; a route id from another account is a 404.
- The bootstrap secret is compared in fixed time, and the app refuses to start without one.
- The container runs as a non-root user (`app`, uid 1654) and Postgres publishes no host port.
- Secrets are never logged; a configuration failure names the key, never the value.

What it deliberately does not do:

- **No CORS policy.** The only client is a desktop app calling over `HttpClient`, not a browser origin,
  so CORS is never consulted. Opening it "just in case" would only hand every page on the internet the
  ability to drive the API with a token it managed to read. A browser client would be an explicit
  decision, not something already left open.
- **No rate limiting** on `/auth/register-device`. The bootstrap secret is the only thing in front of
  it, and a fixed-time comparison does not stop someone from trying a lot of guesses — put it behind a
  proxy that limits requests if the instance is internet-facing. (Flagged in `plans/S2-auth.md` and
  carried forward in S5.)
- **No token rotation or revocation.** Tokens are long-lived (`Jwt__ExpiryDays`, 365 by default) and the
  only revocation is rotating `Jwt__SigningKey`, which invalidates all of them at once.
- **No multi-user auth.** One account, many devices; anyone holding the bootstrap secret joins that
  account.

## Development

Local development runs the app on the host against a Postgres container. `docker-compose.dev.yml` is the
dev-only file — a different port and a different volume from the production stack, so the two never
share state:

```powershell
docker compose -f docker-compose.dev.yml up -d      # localhost:15432, workledger/workledger

# The app fails fast if these are missing. `dotnet run` picks them up from
# Properties/launchSettings.json; set them explicitly for any other launch.
$env:Jwt__SigningKey = "<at least 32 bytes>"
$env:Bootstrap__Secret = "<any>"

dotnet build WorkLedger-Server.slnx
dotnet run --project src/WorkLedger.Server                 # http://localhost:5284
```

The dev connection string (`localhost:15432`) lives in `appsettings.Development.json`, so a Production
run has no localhost fallback to silently fall back to.

### Tests

```powershell
dotnet test WorkLedger-Server.slnx
dotnet test WorkLedger-Server.slnx --filter "FullyQualifiedName~ReportEndpointTests"
```

The data, auth, sync and report tests each spin up their own `Testcontainers.PostgreSql` container, so a
working Docker daemon is required; they never touch `docker-compose.dev.yml` or any real secret. The
`/health` test overrides `Database:MigrateOnStartup=false` and needs no database at all. CI
(`.github/workflows/build.yml`) restores, builds `Release`, runs the suite, and builds the production
image on every push and pull request.

### Schema changes

Run from `src/WorkLedger.Server/` — a design-time factory means no running app is needed:

```powershell
dotnet ef migrations add <Name> -o Data/Migrations
dotnet ef database update
```

The migration is applied automatically the next time the app boots (`Database:MigrateOnStartup`, true by
default), which is the simple choice for a single-instance deployment. It would have to become a separate
step before running more than one API instance against one database, since concurrent `Migrate()` calls
must not race — set `Database__MigrateOnStartup=false` and run `dotnet ef database update` (or
`migrations script`) as part of the deploy. See `src/WorkLedger.Server/Data/README.md`.

### Releases

Conventional Commits (`feat:` minor, `fix:` patch, `chore:`/`docs:`/`refactor:` no bump), one branch and
one PR per plan. A release is a `v`-prefixed tag:

```bash
git tag -a v0.1.0 -m "First self-hostable release"
git push origin v0.1.0
```

`.github/workflows/release.yml` then builds the image, pushes it to
`ghcr.io/dante286/workledger-api` (tagged `0.1.0`, `0.1`, `latest`, with the tag stamped into the
binary's informational version), and opens a GitHub release. Operators upgrade with
`docker compose pull && docker compose up -d`; nothing in CI deploys to a live environment.

## Cross-repo: the contract is vendored

`WorkLedger.Contracts` is authored here and the client keeps a **byte-identical copy** at
`Sync/Contracts/`, with the source commit recorded in its `Sync/Contracts/README.md`. There is no NuGet
package (a cross-repo `ProjectReference` was rejected: client CI checks out only its own repo). So
**changing `WorkLedger.Contracts` is a two-repo change** — re-take the copy and bump the provenance
commit, or the two sides drift silently, which is the exact failure the shared-C#-types design exists to
prevent. Nothing detects automatically that the copy has fallen behind; check with
`git log --oneline <pinned-commit>..HEAD -- src/WorkLedger.Contracts`.

Server work reads the client repo freely but does not commit to it (`plans/README.md`).

### Contract reconciliation

The DTOs in `src/WorkLedger.Contracts/Payloads/EntityPayloads.cs` are reconciled against the client's
`Models/Project.cs`, `Models/TaskItem.cs` and `Models/TimeEntry.cs` (checked 2026-08-11). Notably:
`Project` has no `IsArchived`; `TaskItem` has `Notes` and `IsCompleted` as well as `IsArchived`;
`TimeEntry` stores both `StartTime`/`EndTime` **and** a persisted `DurationSeconds` (not computed-only).
Parent links (`ProjectSyncId`, `TaskSyncId`) resolve by `SyncId`, never by the client's local
autoincrement ids.

`SyncChangeDto.PayloadJson` is **PascalCase** on the wire (`SyncJson.Options`): the client deserializes
with default, case-sensitive options, so switching to web/camelCase defaults would silently produce
all-default entities instead of throwing.
