# WorkLedger Server

The backend for [WorkLedger](https://github.com/dante286/WorkLedger-Client): it holds the authoritative
copy of a user's projects, tasks and time entries, reconciles the changes each device pushes, and
computes the cross-device rollups no single device can. Self-hosted by design — one `docker compose up`
on a homelab box or a small VPS, with your data in a Postgres volume you own and back up.

C# / ASP.NET Core Minimal APIs on .NET 10, EF Core + Npgsql → PostgreSQL, JWT bearer auth, xUnit +
Testcontainers.

- **Deploying it** → [Quickstart](#quickstart-self-hosted-with-docker-compose), then
  [Configuration](#configuration) and [Operations](#operations).
- **Working on it** → [Development](#development). The design record is `plans/` (S0–S9) and
  `plans/README.md`.
- **Coming from a pre-user-accounts install** →
  [Upgrading from a pre-user-accounts deployment](#upgrading-from-a-pre-user-accounts-deployment).

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
  opts out with `.AllowAnonymous()` — only `/`, `/health` and the four endpoints a caller with no token
  needs (`/auth/capabilities`, `/auth/register`, `/auth/claim`, `/auth/login`) do. Adding an endpoint
  cannot accidentally leave it open.
- **Scope always comes from the token.** `ICurrentContext` reads `user_id`/`device_id` out of the
  JWT claims, and every sync and report query filters on that user. Nothing in a request body or URL
  can widen it. Ids in routes and payloads are cross-device `SyncId` Guids, never the server's internal
  int ids, so an id belonging to another user is simply a 404.

A **user account is the top of the tenancy tree**: one `User` row owns many `Device` rows, and every
device syncs the same projects, tasks and time entries back under that user. **Credentials are
user-level; tokens are device-level** — a person signs in *from a device*, and what comes back is a JWT
scoped to that `(user, device)` pair. Registration is gated on the `Bootstrap__Secret` signup secret and
can be switched off entirely with `REGISTRATIONS_ALLOWED=false`.

Nothing is seeded. A fresh database has zero users; the first `POST /auth/register` creates one.

## Quickstart: self-hosted with Docker Compose

Requires Docker with Compose v2. From a clean checkout:

```bash
cp .env.example .env

# Fill in the three required secrets. On Linux/macOS:
#   Jwt__SigningKey    — at least 32 bytes; signs device tokens
#   Bootstrap__Secret  — gates account registration (/auth/register, /auth/claim)
#   POSTGRES_PASSWORD  — the bundled database's password (set it before the first boot)
openssl rand -base64 48   # once per secret
$EDITOR .env

docker compose up -d
```

That builds the API image, starts Postgres, waits for it to pass its healthcheck, then starts the API —
which applies the EF migrations before it serves anything. A fresh host needs no other provisioning
step, and no account: the database comes up empty and the first registration creates its first user.

```bash
curl http://localhost:8080/health      # {"status":"ok","utc":"..."}
curl http://localhost:8080/            # {"name":"WorkLedger.Server","version":"..."}
docker compose ps                      # both services should read "healthy"
```

If the API container exits instead, `docker compose logs api` names the configuration key it is missing
— it refuses to start on a missing signing key, bootstrap secret or database rather than serving
half-configured. Compose itself refuses the `up` if a required variable is absent from `.env`.

### First run: create an account, then sign in from each device

Two steps, and they are different kinds of thing. **Registration is a user-level act** gated by the
signup secret, done once. **Signing in is per device** and is the only source of a token.

Ask the server what its rules are first — a client is expected to do this, and it costs nothing:

```bash
curl http://localhost:8080/auth/capabilities
# {"registrationEnabled":true,"registrationRequiresSecret":true,"minPasswordLength":12,
#  "hasUnclaimedAccount":false,"maxFailedLogins":5,"lockoutMinutes":15}
```

Create the account. The secret goes in the `X-Bootstrap-Secret` header; nothing else on the API reads
it:

```bash
curl -X POST http://localhost:8080/auth/register \
  -H "X-Bootstrap-Secret: $Bootstrap__Secret" \
  -H 'Content-Type: application/json' \
  -d '{"email":"sam\example.com","password":"correct-horse-battery","displayName":"Sam"}'
# 201 {"id":1,"email":"sam\example.com","displayName":"Sam","createdAtUtc":"2026-..."}
```

No token comes back, deliberately: there is no device to bind one to yet. Now sign in from each device,
passing that device's own stable id:

```bash
curl -X POST http://localhost:8080/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"sam\example.com","password":"correct-horse-battery","deviceId":"laptop-01","deviceLabel":"Work laptop"}'
# {"token":"eyJ...","expiresAtUtc":"2027-...","user":{"id":1,"email":"sam\example.com",...}}

curl http://localhost:8080/auth/whoami -H "Authorization: Bearer $token"
# {"userId":1,"email":"sam\example.com","displayName":"Sam","deviceId":"laptop-01"}
```

`deviceId` is the client's own stable per-install identifier. **Enrolment is a side effect of logging
in**: the first login from a `deviceId` creates the device row, and a later login from the same id
refreshes its token, label and last-seen rather than creating a second device. The same `deviceId`
under a *different* user is a different device.

Every other endpoint wants that `Authorization: Bearer <token>` header and returns 401 without it.
Tokens last `Jwt__ExpiryDays` (365 by default) and there is no refresh; what replaces short expiry is
revocation, which is immediate — see
[Security posture](#security-posture-and-deferred-hardening).

Signing in is rate-limited per IP, and an account locks itself after repeated failures, so a wrong
password a handful of times in a row is answered `423` with a `Retry-After` rather than another `401`.
It unlocks itself; nothing needs doing.
## Endpoints

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /` | none | Name and running version (the image's `VERSION` build arg). |
| `GET /health` | none | Liveness. Touches no database, which is why it is also the container's `HEALTHCHECK`. |
| `GET /auth/capabilities` | none | This server's account policy, so a client can render the truth instead of guessing: whether registration is open, whether it needs the secret, the minimum password length, whether an unclaimed account is waiting, and the lockout numbers. Configuration, not secrets. |
| `POST /auth/register` | `X-Bootstrap-Secret` header | Create a user. `201` and the user; **no token** — tokens are device-scoped. `403` registration disabled, `401` bad secret, `400` bad email or password, `409` email taken. |
| `POST /auth/claim` | `X-Bootstrap-Secret` header | Attach an email and password to a pre-user-accounts account, in place, keeping its data and its devices. `409` when there is nothing claimable. Deliberately **not** gated by `REGISTRATIONS_ALLOWED`. |
| `POST /auth/login` | none (credentials in the body) | **The only source of a token.** Enrols the request's `deviceId` under the authenticated user and mints a JWT for that `(user, device)`. `401` for any bad credential, `423` locked out, `429` rate-limited. |
| `GET /auth/whoami` | bearer | The user and device the caller's token resolves to. |
| `POST /auth/password` | bearer | Change the password. Re-verifies the current one (`401` if wrong), then bumps the security stamp — **killing every token, the caller's included** — and hands back a replacement. |
| `GET /auth/devices` | bearer | Every device signed in to this account, with `isCurrent` marking the caller's. |
| `DELETE /auth/devices/{deviceId}` | bearer | Revoke one device's access. `204`; `404` for an id this user does not own. Leaves that device's synced rows alone — they belong to the user, not the device. Removing your own is allowed and means "sign this device out". |
| `POST /auth/logout-all` | bearer | Bump the security stamp: every device signed out at once, with a fresh token returned for the caller. |
| `DELETE /auth/account` | bearer | Hard-delete the account and everything under it. Two independent gates: the current password (`401`) and a typed `confirmEmail` (`400`). Immediate and irreversible. |
| `POST /sync/push` | bearer | Submit a batch of create/update/delete changes; returns one result per change, in request order. |
| `GET /sync/pull?since=<iso-8601>` | bearer | Changes from other devices with `UpdatedAt > since`. Omit `since` for everything. |
| `GET /reports/weekly?weekOf=<yyyy-MM-dd>` | bearer | One ISO week (Monday–Sunday, UTC), across every device of the signed-in user. |
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
| `Jwt__SigningKey` | Signs device tokens. At least 32 bytes. Rotating it invalidates every issued token, so every device signs in again. Not stored in the database — back it up with your dumps. |
| `Bootstrap__Secret` | Gates **registration** (`POST /auth/register`, `POST /auth/claim`) via the `X-Bootstrap-Secret` header, and nothing else. It cannot mint a token; only a password can. Treat it like a password. |
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
| `Jwt__ExpiryDays` | `365` | Device token lifetime. Long on purpose: there is no refresh, and revocation is the security stamp rather than expiry. |
| `Jwt__Issuer` / `Jwt__Audience` | `WorkLedger.Server` / `WorkLedger.Client` | Validated on every request; change both sides together. |
| `REGISTRATIONS_ALLOWED` | `true` | Whether `POST /auth/register` accepts signups at all (`403` when off). `Auth__RegistrationEnabled` is the canonical key and wins if both are set. `POST /auth/claim` ignores this flag by design. |
| `Auth__MinPasswordLength` | `12` | Shortest password `/auth/register`, `/auth/claim` and `/auth/password` accept. Reported by `/auth/capabilities`. |
| `Auth__MaxFailedLogins` | `5` | Consecutive failed sign-ins before an account locks. `0` disables the lockout — it does not mean "lock on the first failure". |
| `Auth__LockoutMinutes` | `15` | How long that lockout lasts. The failure counter resets once the window expires, so an account cannot end up locked permanently. |
| `Auth__LoginRateLimitPerMinute` | `10` | Per-IP limit on `/auth/register`, `/auth/claim` and `/auth/login`; over it is `429` with `Retry-After`. `0` disables it. Partitions on the socket's address — see the reverse-proxy caveat under [Putting it behind TLS](#putting-it-behind-tls). |
| `Auth__StampCacheSeconds` | `30` | How long a token's revocation check is cached. **This is the revocation lag**, and only for revocations that happen elsewhere: the endpoints that revoke evict the entry themselves, so the common case is immediate. `0` checks the database on every authenticated request. |
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

Three read-only, user-scoped endpoints. They are the **authoritative cross-device rollup**: once
several devices sync into one user account, only the server holds the merged picture. (The client's Reports
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
or deleted project/task is a 404 — which is also the answer for one belonging to another user.

**What counts.** A time entry contributes only if it, its task, and that task's project are all alive
(tombstoned rows are excluded along the whole chain) and it has an `EndTime`. Its length is the stored
`DurationSeconds`, falling back to `EndTime − StartTime`. It lands wholly in the UTC day it *started*, so
an entry spanning midnight is not split — the same rule the client's own rollup uses. Entries still
running have no fixed length and are excluded from every total, but each report carries a
`runningEntries` count, so a caller can say "plus one timer still going" instead of silently
under-reporting. The counting rules are stated in full on `ReportService` (rules 1–6), with
`ReportQuery` as their SQL form.

The weekly report's `days` array always has seven entries, zero-filled, so it can be charted directly;
its overall total, per-project, per-task and per-day views are all folded from one aggregation and
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
- The JWT signing key is **not** in the database. Losing `.env` means a new key, which kills every
  token and makes every device sign in again — recoverable, but only if someone still knows a
  password. Back it up (encrypted) alongside the dumps. Losing the `.env` *and* the passwords means
  the data survives in the volume with nothing able to authenticate against it; see
  [Recovering a lost password](#recovering-a-lost-password).

### Upgrading

```bash
git pull                                  # or just edit WORKLEDGER_VERSION in .env
docker compose pull                       # if running a published GHCR tag
docker compose up -d                      # recreates the api container
```

Migrations are applied by the API on boot, so an upgrade that adds a column needs no separate step. Take
a dump first anyway: a migration is the one kind of change that a rollback of the image cannot undo.

### Upgrading from a pre-user-accounts deployment

Releases before this one had **one account**, seeded on first boot, and any device holding the bootstrap
secret could mint itself a token. Now a **user account owns its devices**, and a token comes only from a
password. Your data carries over untouched — the migration renames tables in place, it never copies or
re-creates rows — but the tokens do not, and one HTTP call transfers ownership of the existing account.

**Do it in this order.** `POST /auth/claim` only works while the database holds *exactly one* user row
with no password. Register anybody else first and it answers `409` with nothing left to claim, and the
old account becomes unreachable — its data still there, nothing able to sign in to it.

```bash
# 1. Back up. This is a schema migration; the image rollback cannot undo it.
docker compose exec -T postgres pg_dump -U workledger -Fc workledger > pre-user-accounts.dump

# 2. Upgrade. The API migrates on boot: Accounts -> Users, plus the new credential columns.
docker compose pull
docker compose up -d
docker compose logs api | tail -20        # migration output, then the request log

# 3. Confirm there is something to claim.
curl http://localhost:8080/auth/capabilities
# ..."hasUnclaimedAccount":true...

# 4. Take ownership of it: your email, a password of at least Auth__MinPasswordLength.
curl -X POST http://localhost:8080/auth/claim \
  -H "X-Bootstrap-Secret: $Bootstrap__Secret" \
  -H 'Content-Type: application/json' \
  -d '{"email":"sam\example.com","password":"correct-horse-battery"}'
# 200 {"id":1,"email":"sam\example.com","displayName":"Default","createdAtUtc":"2026-..."}
```

Then **sign every device in again** (`POST /auth/login`, as in
[First run](#first-run-create-an-account-then-sign-in-from-each-device)). Every pre-upgrade token is
dead: they carry no security stamp and are refused on that basis alone, so there is no window in which
an old token still works. The device rows survive the upgrade, so a device signing in with the same
`deviceId` picks up its own row rather than creating a second one, and nothing re-syncs from scratch.

Two details worth knowing:

- **`Account__Name` is gone.** It named the seeded account and there is no seeder any more. If it is
  still in your `.env` it is simply ignored — nothing reads it, and leaving it there is harmless. The
  value it set is not lost: the migration renames that column to `DisplayName`, so it comes back as the
  claimed account's display name (`"Default"` above, if you never changed it).
- **Nothing forces you to close signup**, but you probably want to. Once your account exists, set
  `REGISTRATIONS_ALLOWED=false` in `.env` and `docker compose up -d`: `POST /auth/register` then answers
  `403`, `/auth/capabilities` advertises it so clients hide their registration UI, and the bootstrap
  secret stops mattering for signup. `POST /auth/claim` deliberately keeps working — an operator must
  always be able to claim their own data.

### Recovering a lost password

There is no password reset: the server sends no mail, so there is no reset link to send (see
[Security posture](#security-posture-and-deferred-hardening)). What there is instead is the database,
which you own. Both recoveries below are `psql` against the volume, and both assume you can reach it:

```bash
docker compose exec -it postgres psql -U workledger -d workledger
```

**If yours is the only account**, hand it back to `POST /auth/claim` by clearing its password. A row
with a null `PasswordHash` is the "unclaimed" state the upgrade path uses, and it can never
authenticate, so the row is inert until you claim it:

```sql
UPDATE "Users" SET "PasswordHash" = NULL, "Email" = NULL, "NormalizedEmail" = NULL,
                   "FailedLoginCount" = 0, "LockoutEndUtc" = NULL;
```

Then `POST /auth/claim` with the email and password you want, exactly as in the upgrade section above.
Projects, tasks, time entries and device rows are untouched.

**If there is more than one account**, that path is closed on purpose — "claim" cannot know which
account is yours once the deployment is multi-user. Copy a hash you do know the password for onto the
locked-out row instead, sign in, and change it from the app:

```sql
UPDATE "Users" SET "PasswordHash" = (SELECT "PasswordHash" FROM "Users" WHERE "NormalizedEmail" = 'KNOWN\EXAMPLE.COM'),
                   "FailedLoginCount" = 0, "LockoutEndUtc" = NULL
 WHERE "NormalizedEmail" = 'LOCKED\EXAMPLE.COM';
```

Neither statement bumps `SecurityStamp`, so tokens already issued to that account keep working — which
is usually what you want during a recovery. To sign every one of its devices out as well:

```sql
UPDATE "Users" SET "SecurityStamp" = gen_random_uuid()::text WHERE "NormalizedEmail" = 'LOCKED\EXAMPLE.COM';
```

That one takes up to `Auth__StampCacheSeconds` to bite, because a hand-written `UPDATE` cannot evict the
API's revocation cache the way `/auth/logout-all` does.

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
work correctly; add it only if you want client IPs in the logs.

**One thing does change behind a proxy.** `Auth__LoginRateLimitPerMinute` partitions on the socket's
remote address, so every request appears to come from the proxy and the whole deployment shares one
bucket — ten sign-in attempts a minute across all users rather than each. `ForwardedHeaders` is
deliberately not enabled on a guess: trusting `X-Forwarded-For` from an untrusted hop would let a caller
mint themselves an unlimited bucket per forged IP, which is worse than one shared bucket. Rate-limit at
the proxy, where the real client address is known, and raise or disable the app's limit
(`Auth__LoginRateLimitPerMinute=0`) if the shared bucket gets in the way. The per-account lockout is
unaffected — it keys on the account, not the address. If the proxy runs as its own compose
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

`scripts/smoke-test.ps1` drives a **running deployment** the way one person with two devices would:
read `/auth/capabilities`, register a throwaway account, sign in from two devices, push a project →
task → two time entries from one, pull them on the other, edit from the second and pull the edit back
on the first, push a stale edit and check the server hands back the winner, check every report agrees
with what was pushed (including the CSV header and the 404/400 paths), list the account's devices, and
delete the account again.

```powershell
pwsh ./scripts/smoke-test.ps1              # against a stack that is already up
pwsh ./scripts/smoke-test.ps1 -Up          # docker compose up --build first
pwsh ./scripts/smoke-test.ps1 -KeepAccount # leave the throwaway account behind to poke at
pwsh ./scripts/smoke-test.ps1 -BaseUrl https://workledger.example.com -BootstrapSecret <secret>
```

It reads `API_PORT` and `Bootstrap__Secret` from `.env` when not given, asserts every step, and exits
non-zero on the first failure. This is not a substitute for `dotnet test` — the merge rules, the
counting rules and the auth rules are covered there. What only the smoke test catches is the
deployment: the image, the container configuration, migrations-on-boot, and a token minted by the
container being accepted by it. A few of its assertions can only be made against a real container, and
are there for that reason: registering with a wrong bootstrap secret is a `401`, signing in with a
wrong password is a `401`, and after `DELETE /auth/account` **both** devices' tokens are refused
immediately rather than after `Auth__StampCacheSeconds` — that last one fails if the endpoint's cache
eviction is ever dropped.

**It cleans up after itself**, which the pre-user-accounts version could not: everything it writes
belongs to a throwaway account (`smoke+<timestamp>@example.invalid`) and the final step deletes that
account and its rows. It only ever deletes the account it created. Two things to know before pointing
it at a deployment you care about:

- **Registration has to be open** (`REGISTRATIONS_ALLOWED`), because the run starts by creating that
  account. Against a deployment with signup closed it stops at that step and says so.
- It spends five requests on the per-IP credential rate limit (`Auth__LoginRateLimitPerMinute`, 10 by
  default), so three runs inside the same minute can trip a `429`.

With `-KeepAccount` it skips the deletion and prints the throwaway credentials, so you can sign in and
look around; that account and its rows then stay until you delete them or drop the volume.
## Security posture and deferred hardening

### What it does

- **Nothing is anonymous** except `/`, `/health` and the four endpoints a caller with no token needs
  (`/auth/capabilities`, `/auth/register`, `/auth/claim`, `/auth/login`) — enforced by an authorization
  fallback policy rather than per-endpoint attributes, so a new endpoint cannot be left open by
  omission.
- **Every query is scoped to the token's user**, and a route id belonging to another user is a 404
  rather than a 403, so an id cannot be probed for existence.
- **Passwords are hashed with PBKDF2-SHA512**, salted per password and version-prefixed
  (`Auth/PasswordService.cs`, wrapping `PasswordHasher<T>` from `Microsoft.Extensions.Identity.Core`).
  The version prefix means an iteration-count bump years from now is a detectable format change rather
  than a silent lockout: a hash that is valid but outdated is replaced on the next successful login.
  Passwords are never logged and never returned.
- **Sign-in failures are uniform.** A wrong password, an unknown email and a credential-less account all
  answer the same `401`, so login cannot be used to find out who has an account here. (Registration is
  frank about a duplicate email, because getting that far already required the signup secret. The
  asymmetry is deliberate, and `AuthEndpoints` says so at the line that does it.)
- **Two independent brakes on guessing.** `Auth__MaxFailedLogins` consecutive failures lock one account
  for `Auth__LockoutMinutes` (`423` with `Retry-After`), and `Auth__LoginRateLimitPerMinute` caps the
  credential endpoints per IP (`429` with `Retry-After`). The lockout defends one account; the rate
  limit defends the box against someone spraying many addresses. The lockout's counter resets once its
  window expires, so an account can never end up permanently locked.
- **Tokens can be killed.** Every token carries a `stamp` claim, and every authenticated request
  re-checks the token's device row joined to its user — so a token stops working the moment its device
  is removed, its user is deleted, or the stamp changes (a password change, or `POST /auth/logout-all`).
  That check is cached for `Auth__StampCacheSeconds` (30 by default), and the endpoints that revoke
  evict the entry themselves, so the TTL is only the lag for a revocation that happened elsewhere.
- **Re-authentication for the destructive things.** Changing a password and deleting an account both
  re-verify the current password even though the caller already holds a valid token: a stolen token must
  not be enough to take over or destroy an account. Deletion additionally requires the account's own
  email typed back.
- The bootstrap secret is compared in fixed time, and the app refuses to start without one, without a
  signing key, or without a reachable database.
- The container runs as a non-root user (`app`, uid 1654) and Postgres publishes no host port.
- Secrets are never logged: the request log is method, path and status only — no query string and no
  headers, which is what keeps bearer tokens and the bootstrap secret out of it, since every secret this
  API accepts arrives in a header.

### What it deliberately does not do

- **No email verification.** An email address is an identifier here, not a verified one; the server
  sends no mail and never contacts the address. Anyone holding the signup secret can register any
  address they like.
- **No password reset**, for the same reason: no mail means no reset link to send. Recovery is an
  operator job against the database — see
  [Recovering a lost password](#recovering-a-lost-password). Choose passwords accordingly, and keep the
  signing key and a recent dump somewhere you can reach.
- **No admin, no quotas, no visibility across users.** While `REGISTRATIONS_ALLOWED` is true, anyone
  holding the signup secret can create an account, and **every registered user is a full tenant** with
  their own projects, tasks, entries and reports. There is no role that can list users, cap their
  storage, reset their password or read their rows — not because it is hidden, but because it does not
  exist. If that is not what you want, close signup (`REGISTRATIONS_ALLOWED=false`) once your own
  accounts exist; the flag is advertised on `/auth/capabilities`, so clients hide their registration UI
  rather than offering a form that can only end in a `403`.
- **No CORS policy.** The only client is a desktop app calling over `HttpClient`, not a browser origin,
  so CORS is never consulted. Opening it "just in case" would only hand every page on the internet the
  ability to drive the API with a token it managed to read. A browser client would be an explicit
  decision, not something already left open.
- **No refresh or token rotation.** Tokens are long-lived (`Jwt__ExpiryDays`, 365 by default) by choice,
  with revocation carrying the weight short expiry usually carries — see `JwtTokenService`'s XML doc for
  why that was chosen rather than overlooked. Rotating `Jwt__SigningKey` remains the blunt instrument
  that invalidates every token at once, across every account.
- **No IP-accurate rate limiting behind a reverse proxy** — see
  [Putting it behind TLS](#putting-it-behind-tls).
- **Account deletion is immediate and irreversible.** `DELETE /auth/account` removes the time entries,
  tasks, projects, devices and the user row in one transaction. There is no grace period, no soft delete
  and no undo, and the only export is `/reports/*?format=csv`, which covers recorded time rather than
  the raw rows — take one before deleting, and keep your dumps.
- **No audit trail** beyond the request log. Account deletion writes a single warning line carrying the
  user id and the per-table counts — never the email, for the same reason the request log records no
  headers.
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
