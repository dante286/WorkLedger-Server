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
  DTOs **the desktop client speaks, and nothing else** (S11's rule). It is the source of truth for the
  contract with the client repo (see *Cross-repo* below). Keep it free of server-only packages: the client
  compiles these same files. Shapes only a server-side surface uses — the `/admin/*` JSON API, the portal
  — belong in `WorkLedger.Server` (`Auth/AdminDtos.cs` holds the admin ones and `ResetPasswordRequest`).
- **`src/WorkLedger.Server`** — everything wired in `Program.cs` (no controllers): DI, JWT bearer, a
  `FallbackPolicy` that makes every endpoint authenticated unless it opts out with `.AllowAnonymous()`
  (`/`, `/health`, and everything in `Auth/AuthEndpoints.cs` bar `/auth/whoami` and `/auth/password` —
  note `/auth/password/reset` **is** anonymous, since its whole premise is a caller with no token),
  then `MapAuthEndpoints()` / `MapSyncEndpoints()` / `MapReportEndpoints()`. `Auth/AccountEndpoints.cs`
  holds S8's four signed-in routes (devices, logout-all, account deletion) and `Auth/AdminEndpoints.cs`
  holds S10's operator surface; both are mapped from `MapAuthEndpoints()`, and nothing in either opts
  out of the fallback policy.
  - **The service layer (S11 group A).** The account rules live in `Auth/` services —
    `CredentialCheck`, `RegistrationService`, `AccountSelfService`, `AccountDeletion`, `AdminOperations`,
    `InviteService`, `PasswordResetService`, `ServerSetup` — and both the JSON endpoints and the portal
    pages are thin callers of them. Add a rule to the service, never to one of its two callers.
  - **The web portal (S11).** Razor Pages under `/portal` (`Pages/Portal/`), wired by `Web/PortalSetup.cs`,
    behind `Web:Enabled` (default **false** — off means no page, asset, header or persisted key ring is
    mapped) and a required `Web:PublicBaseUrl` (`WebOptions`, resolved eagerly; the only source of absolute
    links, never the `Host` header). Pages get `PortalUser`/`PortalAdmin` or `AllowAnonymous` by folder
    convention in `PortalSetup`; a page with neither falls to the bearer fallback and fails closed.
    **Tokens travel only in URL fragments or POST bodies, and no GET spends one.**
  - **The JSON account routes are compatibility routes** (`/auth/register`, `/auth/claim`,
    `/auth/password`, `/auth/password/reset`, `/auth/devices`, `/auth/logout-all`, `DELETE /auth/account`):
    they keep working unchanged for clients in the field, and **no new behaviour lands on them** — new
    account features are portal-only. `/admin/*` is *not* one; it stays the operators' scripting interface.
- **`tests/WorkLedger.Server.Tests`** — xUnit, `WebApplicationFactory<Program>` + Testcontainers.

`plans/` holds the S0–S11 build plans (all shipped) and is the design record — `plans/README.md`
explains the cross-repo contract handoff and house style. **`plans/S11-client-handoff.md` is the
authoritative statement of the current wire contract** (it supersedes S10's, which the client never
applied); `plans/S9-client-handoff.md` is still the record of what S6–S8 changed.

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
  migrate-on-boot, a container-minted token, and — when capabilities advertise a `portalUrl` — that the
  image serves portal pages with their CSP and static assets). It writes real rows, deletes the accounts
  it created, and leaves one redeemed `Invites` row per run by design.
- Every optional key in `docker-compose.yml` is passed through **blank** (`${Key:-}`), so a key missing
  from its `environment:` block is silently ignored under compose however `.env` sets it. Adding a
  config key means adding it there too (S11 found four S10 keys that had been missed).

### The auth model in one paragraph

**Credentials are user-level; tokens are device-level.** A person registers once
(`POST /auth/register`, gated by `Bootstrap:Secret` **or**, from S10, a single-use invite token —
`Auth:SignupSecretEnabled` decides whether the secret is still accepted), then signs in *from a device*
(`POST /auth/login`) and gets a JWT scoped to that `(user, device)` pair. Nothing else mints a token.
What replaces short expiry is revocation: `TokenRevocation` re-checks the token's device row joined to
its user on every authenticated request, so a token dies when the device is gone, the user is gone, the
user is suspended (`User.DisabledAtUtc`), or the `stamp` claim no longer matches `User.SecurityStamp`.
The same query carries `User.IsAdmin`, which is emitted as a claim onto the **validated principal** and
is what the `admin` authorization policy gates `/admin/*` on — **never minted into a token**, because a
year-long token must not carry a year-long privilege. So a demotion or a suspension bites on the very
next request. **`Auth:StampCacheSeconds` is the lag**,
but only for a revocation that happened somewhere else, because the endpoints that revoke evict the
cache key themselves.
**The portal adds one user-level cookie scheme (`"Portal"`, `Web/PortalSession.cs`), validated against the
same user row and never accepted by the JSON API** — the default scheme stays JwtBearer, and a bearer token
never authenticates a portal page. The cookie carries `user_id` and `stamp` and no admin claim;
`PortalSession.ValidatePrincipalAsync` checks a cached `user:{id}` snapshot of `(SecurityStamp, IsAdmin,
disabled)` per request, and `TokenRevocation.EvictUserAsync` evicts that key with the user's device keys,
so every revoking path ends portal sessions immediately too. A portal session creates no `Device` row, and
portal sign-in goes through `CredentialCheck`, so it shares the per-user lockout with `/auth/login`.
The three sections below are the detail; the rules live as XML docs on
`CredentialCheck`, `AccountSelfService`, `AccountDeletion`, `AdminOperations`, `TokenRevocation`,
`JwtTokenService`, `PortalSession` and `User`. Since S11 the endpoint classes in `Auth/` are thin callers of those services
and document only the HTTP surface (status codes, where the secret arrives from); a rule's reasoning lives
on the service that enforces it.

### Tenancy: UserId always comes from the credential

The user account is the top of the tenancy tree (S6): one `User` row, many `Device` rows, and the
rows a device syncs belong to the **user** — `OriginDeviceId` is provenance, not ownership.
`ICurrentContext` reads `user_id`/`device_id` from JWT claims; **nothing in a request body or URL may
widen scope**. Portal pages scope on the session cookie's `user_id` (`PortalSession.UserId(User)`) in exactly
the same way — an admin page names a target user, but only behind `PortalAdmin`, exactly as `/admin/*` does.
Every sync and report query filters on `UserId`. Ids exposed in routes and payloads are
cross-device `SyncId` Guids, never the server's internal `int Id` — a route with an unknown or
other-user SyncId is a 404.

Nothing is seeded: a fresh database has zero users, and the first `POST /auth/register` (or
`/portal/setup`) creates one — the first user is the admin.
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
every authenticated request and fails the token if the device is gone, the user id disagrees, the user
is suspended, or the `stamp` claim no longer matches `User.SecurityStamp`. One query, four revocations,
plus the `admin` claim it puts on the principal. It is cached in
`IMemoryCache` for `Auth:StampCacheSeconds` (default 30, `0` disables) — **that TTL is the revocation
lag**, and paths that revoke evict the key directly so the common case is immediate. A token minted with
a stale stamp is dead on arrival, which is why tests that mint their own must read the stored value.

Three login behaviours look wrong until you know why, and all three are stated in full on
`CredentialCheck`'s XML doc (S11 moved them there from the login endpoint): **every failure is the same 401** (unknown email, wrong password and
credential-less row alike — unlike registration's honest 409, because login needs no secret); **the
lockout counter resets when the window expires**, not only on success; and **a 423 writes nothing at
all**, neither incrementing the counter nor re-stamping `LockoutEndUtc`. The last two are what stop an
account being permanently locked. `Auth:MaxFailedLogins`/`Auth:LockoutMinutes` are enforced from the same
`AuthOptions` accessor `/auth/capabilities` advertises them from — don't introduce a second pair.

### Account and device management (`Auth/AccountEndpoints.cs` → `Auth/AccountSelfService.cs`)

S8's four authenticated routes, all scoped by the token alone. Three of them kill tokens and each evicts
the affected `TokenRevocation` cache keys **after** its write commits, so revocation is immediate rather
than lagging `Auth:StampCacheSeconds` — the S8 tests deliberately run with the cache at its default, so
they fail if that eviction is dropped. Two behaviours look wrong and are not:

- **`DELETE /auth/devices/{deviceId}` does not touch the device's synced rows.** Projects/tasks/entries
  belong to the user; `OriginDeviceId` is provenance, not ownership. Removing a device revokes its access
  and nothing else, and the device re-enrolls by logging in again. Deleting your *own* current device is
  allowed and means "sign this device out" — don't "fix" it with a guard.
- **`DELETE /auth/account` is a hard delete with two independent gates** — the current password (401) and
  a typed `ConfirmEmail` (400) — then `TimeEntries` → `TaskItems` → `Projects` → `Devices` → `User`, one
  `ExecuteDeleteAsync` each inside one explicit transaction. The FKs are already `ON DELETE CASCADE`; the
  explicit children-first order is what makes the endpoint independent of that and what produces the
  per-table counts in its single warning line. **That line carries the user id and never the email**, for
  the same reason the request log records only method/path/status. A push racing the delete cannot leave
  orphans — see `AccountDeletion`'s XML doc for why no row lock is taken. That class is the one deletion
  pipeline; `DELETE /admin/users/{id}` runs the same one, inside the transaction holding its last-admin lock.

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
- **Known drift: three trailing, defaulted parameters, and nothing else.** The copy is pinned at server
  **`3d27c86`** (re-vendored 2026-09-15, WL-133), which closed the S6–S8 drift this paragraph once
  described. **Do not re-assert that older description**: `RegisterDeviceRequest`/`Response` being
  deleted and `WhoAmIResponse` being reshaped are on the client already. The client never re-vendored at
  S10's `1e343c8`, and S11 then moved S10's seven added records (`AdminDtos.cs`, `ResetPasswordRequest`)
  out of the library. So the whole of what is outstanding is `RegisterUserRequest.InviteToken` (S10) and
  `AuthCapabilitiesResponse.RegistrationAcceptsInvite` (S10) and `.PortalUrl` (S11). The next pin is
  **`eb743d4`**, and **`plans/S11-client-handoff.md` is that inventory in full**. It supersedes S10's
  handoff for the re-vendor; `plans/S9-client-handoff.md` remains the record of the S6–S8 cutover.
  Re-check with `git log --oneline 3d27c86..HEAD -- src/WorkLedger.Contracts`.
  - **The re-vendor breaks no client call site.** A scratch copy of the client was built and tested
    against the new `AuthDtos.cs` (see the handoff), and every new parameter is last and defaulted. That
    is a constraint S10 and S11 were both built under, not luck. S9's was a genuine cutover; do not
    assume the next one isn't.
  - **Records the client stops calling stay vendored.** `RegisterUserRequest`, `ChangePasswordRequest`,
    `DeviceSummary` and `DeleteAccountRequest` back the JSON compatibility routes, so they stay in the
    library, and so in the client's copy, until the next deliberately wire-breaking release. That is
    not drift.
  - `Reports/ReportDtos.cs` is not drift at all — the client deliberately does not vendor it, because
    it computes its own local reports and never calls `/reports/*`. Nor is
    `SyncItemResult.AuthoritativeOperation` (WL-98), which is on the client and consumed as of WL-101.
  - **Each release re-vendors once, at the end**, deliberately: S6–S8 each changed
    `WorkLedger.Contracts`, and re-taking the copy three times would have handed the client three
    breaking updates instead of one. S9 was that pass for S6–S8, S10E for S10, and S11F (which the
    client will actually apply, covering S10 as well) for S11.
  - **The sync wire format is unchanged since before S6.** Audited file-by-file in S9 (WL-126), again in
    S10 (WL-172) and in S11 (WL-204), where `git diff 3d27c86..HEAD -- src/WorkLedger.Contracts/Sync
    src/WorkLedger.Contracts/Payloads src/WorkLedger.Contracts/Reports` is empty. Not assumed.

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
