# Plan S6 — User accounts: rename `Account` → `User`, password credentials, registration (WL-106)

**Tickets:** [WL-106](https://mnisan.atlassian.net/browse/WL-106), under epic [WL-104](https://mnisan.atlassian.net/browse/WL-104) (*User accounts: user-owned tenancy, login, and device management (server)*).
Subtasks: scope 1–2 → WL-110, 3–4 → WL-111, 5 → WL-112, 6–7 → WL-113, 8–9 → WL-114, 10 (tests) → WL-115.
Scope item 8 also carries [WL-147](https://mnisan.atlassian.net/browse/WL-147) (*Expose `MaxFailedLogins` and
`LockoutMinutes` on `AuthCapabilitiesResponse`*), currently filed as a sibling `Task` under WL-104 rather than
as a subtask of WL-106 — see the note at the end of that scope item. The client-side work this release implies
is tracked separately under epic [WL-105](https://mnisan.atlassian.net/browse/WL-105).

## Context

S2 built auth for a **single self-hosted account**: one seeded `Account` row, devices enrolled with a shared
`Bootstrap__Secret`, and long-lived JWTs carrying an `account_id` claim. It said in as many words that
"multi-user auth (per-user accounts, login, refresh) is the deferred follow-up". This is that follow-up.

**A user account is now the top of the tenancy tree.** One user owns many devices; every device syncs the
user's projects/tasks/time entries back to the same rows. That is exactly the shape `Account` already had, so
this is a **rename plus credentials**, not a new tenancy layer — `Account` becomes `User`, `AccountId` becomes
`UserId`, and the `account_id` claim becomes `user_id`. A separate `User`→`Account` 1:N split (for teams or
shared accounts) was considered and rejected: v1 has no multi-user tenancy to model, and the extra join would
be dead weight in every sync and report query.

S6 lands the model, the credential storage, and account creation. **S7 lands login** — until it does,
`POST /auth/register-device` stays in place as the only way to get a token, so `main` is green and usable at
the end of this plan.

This plan and S7/S8 change `WorkLedger.Contracts`, which is a **two-repo change** (the client vendors a
byte-identical copy at `Sync/Contracts/`). Do not edit the client here; **S9** does the re-vendoring and the
client handoff in one pass.

## Scope

1. **Rename `Account` → `User`, everywhere.**
   - `Data/Entities/Account.cs` → `User.cs`; `WorkLedgerDbContext.Accounts` → `.Users`.
   - `AccountId` → `UserId` on `Device`, `Project`, `TaskItem`, `TimeEntry`, and on every index, FK, query and
     test that names it (`SyncService`, `SyncEntityHandler`, `ReportService`, `ReportQuery`, the endpoint
     files, `WorkLedgerDbContextTests`, `SyncEndpointTests`, `ReportEndpointTests`).
   - `ICurrentContext.AccountId` → `UserId`; the JWT claim `account_id` → `user_id` (`JwtTokenService`,
     `CurrentContext`).
   - `WhoAmIResponse(int AccountId, string DeviceId)` → `WhoAmIResponse(int UserId, string DeviceId)` in
     `WorkLedger.Contracts`.
   - Update the *tenancy* prose in `CLAUDE.md` ("Tenancy: AccountId always comes from the token") and the
     matching section of `README.md`. The invariant is unchanged in substance — scope still comes only from the
     token — so keep the wording and swap the noun.

2. **The migration is a rename, not a rebuild.** Existing deployments have real rows. Hand-edit the generated
   migration if EF proposes drop/create: `RenameTable`, `RenameColumn`, `RenameIndex`, and renamed FK
   constraints. `Account.Name` → `User.DisplayName`. A `docker compose up` on an existing volume must come back
   with every project, task, time entry and device intact.

3. **Credential columns on `User`.**

   | Column | Type | Null | Notes |
   |---|---|---|---|
   | `Email` | `text` | yes | as typed, for display |
   | `NormalizedEmail` | `text` | yes | `ToUpperInvariant`, unique **where not null** |
   | `PasswordHash` | `text` | yes | see below |
   | `SecurityStamp` | `text` | no | Guid string; bumping it revokes outstanding tokens (S7) |
   | `DisplayName` | `text` | yes | was `Account.Name` |
   | `CreatedAt` | `timestamptz` | no | existing |
   | `UpdatedAt` | `timestamptz` | yes | new |

   `Email`/`PasswordHash` are **nullable on purpose**, and the reason is the one thing to document as an XML
   doc on `User`: a row with a null `PasswordHash` is the **pre-S6 legacy tenant awaiting `POST /auth/claim`**
   (scope item 6). It can never authenticate — login requires a non-null hash — and the claim endpoint is its
   only writer. Once claimed, both are non-null forever. The unique index on `NormalizedEmail` is a Postgres
   partial index (`.HasFilter(...)` on `NormalizedEmail IS NOT NULL`) so unclaimed rows don't collide.

4. **Password hashing: `PasswordHasher<User>` from `Microsoft.Extensions.Identity.Core`.** Add that package
   only — **not** `Microsoft.AspNetCore.Identity.EntityFrameworkCore` and not `AddIdentity`. Rationale to
   record as an XML doc: we want PBKDF2-SHA512 with a versioned hash format and a rehash-needed signal, not
   Identity's schema, its cookie stack, or its opinions about our custom JWT pipeline. Wrap it in a small
   `Auth/PasswordService.cs` so the rest of the code never references the Identity type directly.

5. **`POST /auth/register`** (AllowAnonymous). Body `RegisterUserRequest(string Email, string Password, string? DisplayName)`.
   Gates in this order, each with its own distinct failure:
   - **Registration disabled** → `403` `{ error = "Registration is disabled on this server." }`. Driven by
     `Auth:RegistrationEnabled` (default `true`), which also accepts the flat env var `REGISTRATIONS_ALLOWED`
     as an alias so operators can set the name they expect; `Auth__RegistrationEnabled` is the canonical key
     and wins if both are set. **403 rather than 404**: `GET /auth/capabilities` already advertises the flag,
     so hiding the route buys nothing and costs an operator a confusing debugging session.
   - **Signup secret** → `401` unless the `X-Bootstrap-Secret` header matches `Bootstrap:Secret`, compared with
     `CryptographicOperations.FixedTimeEquals` exactly as `/auth/register-device` does today. The secret's job
     transfers from device bootstrap to signup; keeping the same config key and header means operators change
     nothing in `.env` and existing deployments keep their posture.
   - **Validation** → `400`: email must be non-empty and contain `@` with a non-empty local and domain part
     (deliberately not a regex RFC-5322 attempt); password length within `Auth:MinPasswordLength` (default 12)
     and 256. **No composition rules** — per NIST 800-63B, length is the control that matters and complexity
     theater pushes people toward `Password1!`.
   - **Duplicate email** → `409` `{ error = "An account with that email already exists." }`. This does enumerate
     accounts; that is an accepted tradeoff behind a shared secret on a self-hosted box, and the honest error
     is worth far more than the leak costs. **Login (S7) must not enumerate** — note the asymmetry in a comment
     so nobody "fixes" it later.
   - Success → `201` with `UserSummary(int Id, string Email, string? DisplayName, DateTime CreatedAtUtc)`.
     **No token.** Tokens are device-scoped and minted by login (S7); registration is a user-level act and has
     no device to bind to.

6. **`POST /auth/claim`** (AllowAnonymous, `X-Bootstrap-Secret` gated). The upgrade path for a deployment that
   already has the seeded v1 account and real data. Body is the same `RegisterUserRequest`. Behavior:
   - Exactly one user row exists **and** its `PasswordHash` is null → attach email/password/display name to
     **that** row, so all existing projects/tasks/entries/devices carry over unchanged. `200` + `UserSummary`.
   - No unclaimed row (fresh install, or already claimed) → `409` `{ error = "Nothing to claim." }` pointing at
     `/auth/register`.
   - Unlike `/auth/register`, this is **not** subject to `Auth:RegistrationEnabled` — an operator who disabled
     registration must still be able to claim their own existing data.

7. **Stop seeding.** Remove `SeedV1AccountAsync` and the `Account:Name` config read from `Program.cs`. A fresh
   database now starts with **zero users** and the first `POST /auth/register` creates one. `Account__Name`
   becomes obsolete: leave it undocumented here and let S9 remove it from `.env.example`/`docker-compose.yml`.

8. **`GET /auth/capabilities`** (AllowAnonymous) →
   `AuthCapabilitiesResponse(bool RegistrationEnabled, bool RegistrationRequiresSecret, int MinPasswordLength, bool HasUnclaimedAccount, int MaxFailedLogins, int LockoutMinutes)`.
   This is what lets the client GUI **hide its registration UI** when the server has it switched off, which is
   the stated requirement — a client that only learns about the flag from a 403 has already shown the user a
   form that cannot work. `HasUnclaimedAccount` lets it offer "Claim this server" instead of "Register".

   The last two fields are [WL-147](https://mnisan.atlassian.net/browse/WL-147), and they are here for exactly
   the reason `MinPasswordLength` already is: so the client can tell the user the truth about this server's
   policy instead of guessing at it. The client warns before a sign-in attempt that would lock the account
   ("two more failures will lock this account for 15 minutes" — client `plans/08` item 4h, `plans/09` item 2a),
   and hardcoding 5 and 15 makes that warning a lie on any deployment where the operator changed
   `Auth:MaxFailedLogins` or `Auth:LockoutMinutes`. Two things to record as an XML doc on the DTO:
   - **These are configuration, not secrets.** The endpoint is `AllowAnonymous`, and a static policy value
     tells an attacker only what five wrong guesses would tell them anyway.
   - **It is deliberately *not* a remaining-attempts count.** A remaining count could only be answered for an
     account that exists, so returning one would enumerate accounts and undercut the uniform 401 S7 goes out
     of its way to produce on login. Static policy leaks nothing; per-account state leaks everything.

   **Sequencing wrinkle, resolved here rather than left implicit.** S6 ships this endpoint, but the lockout it
   describes is not enforced until S7. The resolution: **S6 defines the DTO with both fields now and reads
   `Auth:MaxFailedLogins` / `Auth:LockoutMinutes` from configuration with the same defaults S7 will enforce
   (5 and 15).** Reading a config value before the code that acts on it exists is harmless; the alternative
   — adding the fields in S7 — makes `AuthCapabilitiesResponse` change shape twice, which both S9's contract
   inventory and the client's single vendoring pass assume it does not. Keep one source for the two numbers:
   whatever config accessor S6 adds to serve them is the accessor S7's enforcement reads, and **S7 scope item 4
   cross-references this item** so the defaults cannot drift apart.

   > **Ticket placement (noted, not acted on).** WL-147 is filed as a `Task` under epic WL-104, alongside
   > WL-106–109 — a peer of the four plans rather than part of one. It is scope for *this* item, so a subtask
   > of WL-106 next to WL-114 would model it better and would keep S6's subtask list complete. That is a
   > re-parent rather than a plan edit; left as filed, and recorded here so it reads as a decision rather than
   > an oversight.

9. **Keep `/auth/register-device` alive, but fenced.** It is still the only token source until S7. Point it at
   the single user (`Users.OrderBy(u => u.Id).First()`) as before, and add one guard: if **more than one** user
   row exists, return `410 Gone` with a message naming `/auth/login`. Silently attaching a new device to
   whichever account happens to be oldest is the kind of bug that is invisible until someone else's time
   entries show up on your laptop. Comment it as deleted-by-S7.

10. **Tests** (Testcontainers + `WebApplicationFactory`):
    - Registration: happy path returns 201 and persists a normalized email and a hash that is **not** the
      plaintext; duplicate email (differing only in case) → 409; short password → 400; malformed email → 400;
      wrong/missing secret → 401; `Auth:RegistrationEnabled=false` → 403.
    - `PasswordService` round-trip: verify succeeds for the right password, fails for the wrong one, and two
      registrations of the same password produce **different** hashes (salted).
    - Claim: against a database whose only user has a null hash → 200 and the user keeps its `Id` (assert a
      pre-existing `Project` row is still reachable under it); a second claim → 409.
    - `/auth/capabilities` reflects `Auth:RegistrationEnabled` in both positions, and reports
      `MaxFailedLogins`/`LockoutMinutes` *from configuration* — the defaults (5/15) against a bare test host,
      and the overridden values when the host sets them. That second assertion is the one that catches the
      fields being hardcoded into the response instead of read.
    - `/auth/register-device` → 410 once two users exist.
    - The existing sync/report suites pass unchanged apart from the `AccountId` → `UserId` rename — that is the
      real proof the rename didn't widen anyone's scope.
    - `AuthWebAppFixture`/`TestConfig` need updating: no account is seeded any more, so fixtures must create
      their user through the API (or a helper) rather than assuming row 1 exists.

## Out of scope

- **Login.** No `POST /auth/login`, no password verification path into a token — S7.
- **Revocation.** `SecurityStamp` is stored and defaulted here but nothing reads it yet — S7 wires the
  `OnTokenValidated` check, S8 bumps it.
- **Account deletion, device listing, password change** — S8.
- **Re-vendoring `WorkLedger.Contracts` into the client, `.env.example`/compose/README/smoke-test updates** —
  S9. This plan may leave those docs describing the old flow; S9 rewrites them in one pass.
- **Email verification and password reset.** Both need SMTP, which this deployment doesn't have. Deferred, and
  worth an explicit note in the README (S9) so nobody assumes a forgotten password is recoverable.
- **Roles/permissions.** One user owns their own data; there is nothing to authorize beyond identity.

## Acceptance criteria

- `Account` no longer exists as a type, column, claim, or DTO field anywhere in `src/` or `tests/`; the sync
  and report suites pass with only the rename applied.
- `dotnet ef database update` against a database holding pre-S6 data preserves every row — verified by a test
  that seeds the old shape, migrates, and reads the data back under the renamed columns.
- `POST /auth/register` creates a user behind the signup secret, refuses when `REGISTRATIONS_ALLOWED=false`,
  and never stores a plaintext or unsalted password.
- `POST /auth/claim` converts the legacy seeded account in place, keeping its data and devices.
- `GET /auth/capabilities` reports the registration flag, whether a secret is required, the minimum password
  length, whether an unclaimed account exists, and the lockout policy (`MaxFailedLogins`/`LockoutMinutes`, read
  from configuration) — all accurately, and all without a token.
- A fresh database starts with zero users; nothing is seeded.
- `/auth/register-device` still issues a working token when exactly one user exists, and 410s beyond that.

## Suggested commit message

```
feat(auth)!: make the user account the tenancy root with email/password credentials

BREAKING CHANGE: the `account_id` JWT claim is now `user_id` and WhoAmIResponse.AccountId is
WhoAmIResponse.UserId. Tokens issued before this change no longer validate.
```

## Prompt for Claude Code

> Read `CLAUDE.md` first, especially the tenancy and cross-repo sections. Rename the `Account` entity to `User`
> across `src/` and `tests/`: `AccountId` → `UserId` on `Device`/`Project`/`TaskItem`/`TimeEntry` and every
> index, query and test, `ICurrentContext.AccountId` → `UserId`, the `account_id` JWT claim → `user_id`, and
> `WhoAmIResponse.AccountId` → `UserId` in `WorkLedger.Contracts`. Generate the EF migration and **hand-edit it
> into rename operations** if EF proposes drop/create — existing deployments hold real data. Add nullable
> `Email`/`NormalizedEmail`/`PasswordHash`, a non-null `SecurityStamp`, `DisplayName` (was `Name`) and
> `UpdatedAt` to `User`, with a partial unique index on `NormalizedEmail` where it is not null; document on the
> type why the credential columns are nullable (a null `PasswordHash` is the unclaimed pre-S6 tenant). Add
> `Microsoft.Extensions.Identity.Core` and wrap `PasswordHasher<User>` in an `Auth/PasswordService.cs` — do not
> add ASP.NET Core Identity itself. Implement `POST /auth/register` (AllowAnonymous; 403 when
> `Auth:RegistrationEnabled` — aliased to the `REGISTRATIONS_ALLOWED` env var — is false, 401 without a
> matching `X-Bootstrap-Secret`, 400 on a bad email or a password under `Auth:MinPasswordLength` (default 12),
> 409 on a duplicate normalized email, 201 + `UserSummary` otherwise; no token), `POST /auth/claim` (same body,
> secret-gated but ignoring the registration flag, attaches credentials to the single credential-less legacy
> user or 409s), and `GET /auth/capabilities` (AllowAnonymous, reporting the registration flag, whether a
> secret is required, the minimum password length, whether an unclaimed account exists, and the lockout policy
> — `Auth:MaxFailedLogins` (5) and `Auth:LockoutMinutes` (15), read from configuration here even though S7 is
> what enforces them, so the contract settles in one plan and the client vendors it once. The client GUI hides
> its registration UI off this and warns a user before an attempt that would lock their account; it is
> deliberately static policy rather than a remaining-attempts count, which could only be answered for an
> account that exists and would therefore enumerate accounts). Remove the v1 account seeding from `Program.cs`
> so a fresh database has zero users. Leave `/auth/register-device` working against the single user — it is
> the only token source until S7 — but make it return 410 Gone naming `/auth/login` once more than one user
> exists. Write Testcontainers tests
> for every branch above plus a migration test proving pre-S6 data survives, and update `AuthWebAppFixture` /
> `TestConfig`, which currently assume a seeded account. Do not touch the client repo, `.env.example`,
> `docker-compose.yml`, `README.md`'s deployment sections or `scripts/smoke-test.ps1` — S9 does those.
