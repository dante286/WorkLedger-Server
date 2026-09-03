# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```powershell
# Dev Postgres (required before running the app — it migrates on boot)
docker compose -f docker-compose.dev.yml up -d      # localhost:15432, workledger/workledger

# Secrets must be set or the app refuses to start (Program.cs fails fast).
# `dotnet run` picks these up from Properties/launchSettings.json; set them explicitly otherwise.
$env:Jwt__SigningKey = "<>= 32 bytes>"
$env:Bootstrap__Secret = "<any>"

dotnet build WorkLedger-Server.slnx
dotnet run --project src/WorkLedger.Server                 # http://localhost:5284
dotnet test  WorkLedger-Server.slnx

# Production stack (distinct from the dev compose above: own port, own volume, API in a container)
cp .env.example .env                                       # then fill in the three required secrets
docker compose up -d                                       # postgres -> healthy -> api migrates -> serves
pwsh ./scripts/smoke-test.ps1                               # end-to-end: register, push, pull, report

# One test / one class
dotnet test WorkLedger-Server.slnx --filter "FullyQualifiedName~ReportEndpointTests"
dotnet test WorkLedger-Server.slnx --filter "FullyQualifiedName~SyncEndpointTests.Push_HigherVersion_Overwrites"

# Migrations — run from src/WorkLedger.Server/ (a design-time factory means no running app needed)
dotnet ef migrations add <Name> -o Data/Migrations
dotnet ef database update
```

Tests need a working Docker daemon: the data/auth/sync/report tests each spin up their own
`Testcontainers.PostgreSql` container. They never touch `docker-compose.dev.yml` or real secrets. The
`/health` test overrides `Database:MigrateOnStartup=false` so it needs no database at all.

## Architecture

Three projects (`WorkLedger-Server.slnx`), .NET 10, Minimal APIs, EF Core + Npgsql:

- **`src/WorkLedger.Contracts`** — dependency-free (no ASP.NET, no EF) class library holding the wire
  DTOs. It is the source of truth for the contract with the client repo (see *Cross-repo* below). Keep it
  free of server-only packages: the client compiles these same files.
- **`src/WorkLedger.Server`** — everything wired in `Program.cs` (no controllers): DI, JWT bearer, a
  `FallbackPolicy` that makes every endpoint authenticated unless it opts out with `.AllowAnonymous()`
  (`/`, `/health`, and everything in `Auth/AuthEndpoints.cs` bar `/auth/whoami`), then
  `MapAuthEndpoints()` / `MapSyncEndpoints()` / `MapReportEndpoints()`.
- **`tests/WorkLedger.Server.Tests`** — xUnit, `WebApplicationFactory<Program>` + Testcontainers.

`plans/` holds the S0–S9 build plans (S0–S7 shipped; S8–S9 pending) and is the design record —
`plans/README.md` explains the cross-repo contract handoff and house style.

### Deployment (`Dockerfile`, `docker-compose.yml`)

S5. `README.md` is the operator doc — env vars, backups, TLS, troubleshooting — and is the place to
document deployment behaviour rather than here. Three things that bite when editing these:

- The runtime image is Alpine, so .NET runs **invariant-globalization**. Every culture-sensitive parse is
  already pinned to `InvariantCulture` (`ReportRange`, `SyncTime`); adding one that isn't would only fail
  in the container. Alpine also supplies the busybox `wget` that `HEALTHCHECK` uses.
- All config resolution for the database goes through `PostgresConnection`, and a **blank** value counts
  as unset — compose renders an unset optional override as `""`. `Program.cs` resolves `JwtOptions`,
  `BootstrapOptions` and `PostgresConnection` eagerly right after `Build()` so misconfiguration exits the
  container naming the key instead of 500ing on a device's first sync.
- `scripts/smoke-test.ps1` is the only test that exercises the deployment (image, compose wiring,
  migrate-on-boot, a container-minted token). It writes real rows and does not clean up.

### Tenancy: UserId always comes from the token

The user account is the top of the tenancy tree (S6): one `User` row, many `Device` rows.
`ICurrentContext` reads `user_id`/`device_id` from JWT claims; **nothing in a request body or URL may
widen scope**. Every sync and report query filters on `UserId`. Ids exposed in routes and payloads are
cross-device `SyncId` Guids, never the server's internal `int Id` — a route with an unknown or
other-user SyncId is a 404.

Nothing is seeded: a fresh database has zero users, and the first `POST /auth/register` creates one.
A `User` row whose `PasswordHash` is null is the **unclaimed pre-S6 tenant** awaiting
`POST /auth/claim`; it can never authenticate, and that endpoint is the column's only writer. Read
`User`'s XML doc before treating those nullable credential columns as an oversight.

### Tokens: `POST /auth/login` is the only source, and the stamp is what kills one

S7 deleted `/auth/register-device`; **enrollment is now a side effect of logging in**. Login takes the
client's per-install `DeviceId`, upserts the `Device` row under the authenticated user, and mints a JWT
carrying `sub` (the device row id), `user_id`, `device_id` and `stamp`. `Bootstrap:Secret` still gates
`/auth/register` and `/auth/claim` but can no longer mint a token — only a password can.

Tokens are deliberately **long-lived** (`Jwt:ExpiryDays`, default 365) with **no refresh tokens**; see
`JwtTokenService`'s XML doc for why that was chosen rather than overlooked. What replaces short expiry is
revocation: `TokenRevocation.OnTokenValidatedAsync` loads the token's device row joined to its user on
every authenticated request and fails the token if the device is gone, the user id disagrees, or the
`stamp` claim no longer matches `User.SecurityStamp`. One query, three revocations. It is cached in
`IMemoryCache` for `Auth:StampCacheSeconds` (default 30, `0` disables) — **that TTL is the revocation
lag**, and paths that revoke evict the key directly so the common case is immediate. A token minted with
a stale stamp is dead on arrival, which is why tests that mint their own must read the stored value.

Three login behaviours look wrong until you know why, and all three are stated in full on
`AuthEndpoints`' login doc: **every failure is the same 401** (unknown email, wrong password and
credential-less row alike — unlike registration's honest 409, because login needs no secret); **the
lockout counter resets when the window expires**, not only on success; and **a 423 writes nothing at
all**, neither incrementing the counter nor re-stamping `LockoutEndUtc`. The last two are what stop an
account being permanently locked. `Auth:MaxFailedLogins`/`Auth:LockoutMinutes` are enforced from the same
`AuthOptions` accessor `/auth/capabilities` advertises them from — don't introduce a second pair.

### Sync (`src/WorkLedger.Server/Sync/`)

`/sync/push` and `/sync/pull` implement last-write-wins reconciliation. **The merge rules are stated
once, in full, in the XML doc comment on `SyncService` (rules A–F), and are mirrored by the client and
by `plans/S3-sync-endpoints.md`. Read that comment before touching merge behaviour, and change all
three together.** Rules that look like bugs but are not:

- Deletes are accepted at `>=` (not `>`) and stored at `existing.Version + 1` — forced by client
  behaviour (WL-93). This is the one deliberate deviation from strict LWW.
- Replays are accepted *no-ops with no EF write at all*. `WorkLedgerDbContext.StampUpdatedAt()` stamps
  `UpdatedAt` on every modified synced row, and `UpdatedAt` is the pull cursor — an incidental write
  re-broadcasts the row to every other device.
- Push results are **positional** (one per input change, in request order), because one batch can carry
  create → update → delete for the same entity.
- A batch is applied parents-first (project → task → time entry via `ISyncEntityHandler.OrderRank`) but
  answered in request order; `SaveChangesAsync` runs per item inside one transaction so a later change
  can see an earlier one.

`SyncService` is entity-agnostic; all per-type knowledge lives in the `SyncEntityHandlers.ByEntityType`
registry in `SyncEntityHandler.cs` — add a synced entity there, not by branching in the merge loop.

Wire-format landmines: `SyncEntityTypes`/`SyncOperations` string values are fixed by what the client has
already written into its local queue and must not be renamed. `SyncJson.Options` deliberately emits
**PascalCase** — the client deserializes with default case-sensitive options, so switching to web/camelCase
defaults silently produces all-default entities instead of throwing. All timestamps cross the boundary
through `SyncTime.ToUtc` (columns are `timestamptz`; Npgsql throws on non-UTC `DateTimeKind`, and the
client's SQLite values arrive as `Unspecified`).

### Reports (`src/WorkLedger.Server/Reports/`)

Read-only rollups over the same data — the authoritative *cross-device* view (the client computes its own
local one separately and does not consume these). Counting rules live in one place: the XML doc on
`ReportService` (rules 1–6), with `ReportQuery` as their SQL form. Summary: tombstones exclude the whole
entry→task→project chain; unresolvable parents don't count; running entries (`EndTime == null`) are
excluded but reported as a `runningEntries` count; length is stored `DurationSeconds` falling back to
`EndTime − StartTime`; an entry lands wholly in the UTC day it started. `?format=csv` (or an explicit
`Accept: text/csv`; the query param wins, `*/*` stays JSON) returns RFC 4180; an unrecognized `format`
is a 400.

## Cross-repo: the client at `E:\GitHub-Repos\WorkLedger-Client`

The client is a local sibling repo — **read it** when touching anything on the wire. It's an Avalonia
(.NET 10) offline-first desktop app on SQLite; the pieces that talk to this server are
`Sync/ApiSyncProvider.cs`, `Services/SyncOrchestrator.cs`, `Services/LocalDbService.cs`, and
`Models/SyncQueue.cs`. Its own `CLAUDE.md` is **out of date on sync** (it still describes a planned FastAPI
backend and empty `Sync/` placeholders) — trust the client's code and `Sync/Contracts/README.md` over it.

### The contract is vendored, not packaged

This repo publishes no NuGet package, so the client keeps a **byte-identical copy** of
`src/WorkLedger.Contracts/` at `Sync/Contracts/`, with the source commit recorded in
`Sync/Contracts/README.md`. A cross-repo `ProjectReference` was rejected (client CI checks out only its
own repo). Consequences:

- **Changing `WorkLedger.Contracts` is a two-repo change.** The copy must be re-taken and the provenance
  commit in the client's `Sync/Contracts/README.md` bumped, or the two sides drift silently — the exact
  failure the shared-C#-types design exists to prevent.
- The client's `Tests/WorkLedger.Tests/SyncContractGuardTests.cs` only guards the vendored copy against
  the *client's* own constants and payload shapes. Nothing automatically detects that the copy has fallen
  behind this repo.
- **Known drift as of this writing:** the copy is pinned at server commit `3e6e955`; since then this repo
  added `SyncItemResult.AuthoritativeOperation` (WL-98), `Reports/ReportDtos.cs`, S6's account contract —
  `WhoAmIResponse.AccountId` renamed to `UserId` (a breaking change), plus the new `RegisterUserRequest`,
  `UserSummary` and `AuthCapabilitiesResponse` — and S7's login contract: `RegisterDeviceRequest` and
  `RegisterDeviceResponse` **deleted**, the new `LoginRequest`, `LoginResponse` and
  `ChangePasswordRequest`, and `WhoAmIResponse` widened to `(UserId, Email, DisplayName, DeviceId)`.
  None of it is on the client.
  **S9 re-vendors all of it in one pass**, deliberately: S6–S8 each change `WorkLedger.Contracts`, and
  re-taking the copy three times would hand the client three breaking updates instead of one. Re-check
  with `git log --oneline <pinned-commit>..HEAD -- src/WorkLedger.Contracts`.

### Server comments that may have gone stale

Several XML docs here describe client gaps and name the ticket that would close them. Some have since
landed on client `main` — **verify against the client repo before treating them as current, and before
"fixing" server code that is accommodating them:**

- **WL-91** (parent SyncIds) — the `validate:` messages in `SyncEntityHandler.cs` say the client doesn't
  carry `ProjectSyncId`/`TaskSyncId`. It now does (`Models/TaskItem.cs`, `Models/TimeEntry.cs`).
- **WL-93** (delete version bump) — `SyncService` rule D says the `>=` delete accommodation "can be
  reverted to a plain `>` once WL-93 ships". It has shipped (`LocalDbService.StampForDelete`). Reverting
  is still a breaking change for any client build older than that, so it's a decision, not a cleanup.
- **WL-94 / WL-98** — the client half is implemented in `ApiSyncProvider.MapPushResults` (positional
  zip, `Accepted == false` treated as final, `RequiresFullPull` on a server win).

`ApiSyncProvider`'s header comment lists the server behaviours it depends on — positional results,
tombstones coming back at a *higher* version, and "accepted" not implying the server wrote anything.
Breaking any of those breaks the client silently.

Per `plans/README.md`, server work does **not** modify the client repo — read it, coordinate changes, but
land client edits as their own client-repo work.

## Conventions

- Non-obvious decisions are documented as XML doc comments on the type that owns the rule, with the
  reasoning and the cross-repo ticket (WL-##) that forces it. Follow that pattern rather than adding
  inline `//` notes or a separate doc.
- Conventional Commits (`feat:` minor, `fix:` patch, `chore:`/`docs:`/`refactor:` no bump). One branch
  and one PR per plan, off `main`. `.github/pull_request_template.md` expects a test plan of what was
  actually run.
- Read the client repo freely; don't commit to it as part of server work (see *Cross-repo* above).
