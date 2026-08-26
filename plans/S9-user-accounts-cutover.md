# Plan S9 — Cutover: contracts, deployment config, docs, smoke test, client handoff (WL-109)

**Tickets:** [WL-109](https://mnisan.atlassian.net/browse/WL-109), under epic [WL-104](https://mnisan.atlassian.net/browse/WL-104) (*User accounts: user-owned tenancy, login, and device management (server)*).
Subtasks: scope 1 → WL-126, 2 → WL-127, 3 → WL-128, 4 and 7 → WL-129, 5 → WL-130, 6 → WL-131, 8 → WL-132. The client-side work this release implies is tracked separately under epic [WL-105](https://mnisan.atlassian.net/browse/WL-105).

## Context

S6–S8 changed the wire contract three times (user DTOs added, `RegisterDeviceRequest`/`Response` deleted,
`WhoAmIResponse` reshaped) and left `README.md`, `.env.example`, `docker-compose.yml`, `CLAUDE.md` and
`scripts/smoke-test.ps1` describing a world where a shared secret gets you a token and there is exactly one
account. S9 is the pass that makes the repo tell the truth again, and hands the client the change list.

The reason this is its own plan rather than a paragraph in each of the previous three: `WorkLedger.Contracts`
is **vendored** into the client as a byte-identical copy with a recorded provenance commit
(`Sync/Contracts/README.md`), and nothing automatically detects that the copy has fallen behind. Re-vendoring
after each of S6/S7/S8 would mean three client PRs chasing a moving contract. One cutover at the end, against
a settled shape, is the right unit — and it is the moment the client goes from "sends a static token" to
"has a login screen", which is a client-side project of its own.

**This plan does not modify the client repo** (`plans/README.md`: server work reads the client, coordinates,
but lands client edits as client-repo work). It produces the handoff — the re-vendorable contract, the exact
list of what breaks, and the tickets — not the client code.

## Scope

1. **Settle and audit `WorkLedger.Contracts`.** Before anything else, diff the project against the client's
   vendored copy and write down the final inventory:
   - Added: `RegisterUserRequest`, `UserSummary`, `AuthCapabilitiesResponse`, `LoginRequest`, `LoginResponse`,
     `ChangePasswordRequest`, `DeviceSummary`, `DeleteAccountRequest`.
   - `AuthCapabilitiesResponse` is
     `(bool RegistrationEnabled, bool RegistrationRequiresSecret, int MinPasswordLength, bool HasUnclaimedAccount, int MaxFailedLogins, int LockoutMinutes)`
     — **six fields, not the four S6 originally sketched.** The last two are
     [WL-147](https://mnisan.atlassian.net/browse/WL-147), added in S6 (scope item 8) precisely so the shape
     settles in one plan. Confirm they are actually present before writing the inventory down: the client
     vendors this record exactly once, and re-opening it afterwards is a two-repo change.
   - Removed: `RegisterDeviceRequest`, `RegisterDeviceResponse`.
   - Changed: `WhoAmIResponse`.
   - Unchanged (verify, don't assume): everything under `Sync/` and `Payloads/`. **The sync wire format must
     come through S6–S8 untouched** — same `SyncJson.Options` PascalCase, same `SyncEntityTypes`/
     `SyncOperations` strings, same positional push results. If any of that moved, it was an accident; fix it
     here.
   - Confirm the new DTOs stay dependency-free (no ASP.NET, no EF) — the client compiles these same files.

2. **`.env.example` and `docker-compose.yml`.**
   - Remove `Account__Name` (S6 deleted the seeder). If an operator still has it set, it is now ignored —
     say so in the README upgrade note rather than silently dropping it.
   - Add, with the same commenting style as the existing entries: `REGISTRATIONS_ALLOWED` (and its canonical
     `Auth__RegistrationEnabled` form), `Auth__MinPasswordLength`, `Auth__MaxFailedLogins`,
     `Auth__LockoutMinutes`, `Auth__LoginRateLimitPerMinute`, `Auth__StampCacheSeconds`.
   - Rewrite the `Bootstrap__Secret` comment: it now gates **registration** (`POST /auth/register`,
     `POST /auth/claim`), not token issuance. Note that once your users exist, setting
     `REGISTRATIONS_ALLOWED=false` closes signup entirely and the secret stops mattering.
   - Rewrite the `Jwt__SigningKey` comment: rotating it still invalidates every token, but recovery is now
     "log in again", not "re-run device registration".
   - Compose passes the new variables through with defaults; keep the `:?` fail-the-up treatment for secrets
     only — none of the new knobs is required.

3. **`README.md`** (the operator doc — deployment behavior is documented here, not in `CLAUDE.md`):
   - **Scope-comes-from-the-token** section: `account_id` → `user_id`, and the model is now "a user account
     owns many devices" rather than "one seeded account".
   - **First-run**: register an account (curl with `X-Bootstrap-Secret`), then log in from each device. Replace
     the `register-device` example and the `{"accountId":1,...}` whoami output.
   - **Endpoint table**: drop `/auth/register-device`; add `/auth/register`, `/auth/claim`,
     `/auth/capabilities`, `/auth/login`, `/auth/password`, `/auth/devices` (GET and DELETE),
     `/auth/logout-all`, `DELETE /auth/account`, with their auth requirements.
   - **Config table**: the new `Auth__*` rows; `Account__Name` removed.
   - **A new "Upgrading from a pre-user-accounts deployment" section** — the most valuable thing in this plan
     for anyone actually running it. Back up the volume; `docker compose pull && up -d`; the migration renames
     tables in place and keeps all data; `POST /auth/claim` with your email and a password to take ownership
     of the existing account; every device logs in again (old tokens are dead); `REGISTRATIONS_ALLOWED=false`
     afterwards if you want the door shut.
   - **Security section**: replace "No multi-user auth. One account, many devices; anyone holding the
     bootstrap secret joins that account." Cover what is true now — password hashing (PBKDF2 via
     `PasswordHasher`), lockout and rate limiting, the security-stamp revocation and its
     `Auth__StampCacheSeconds` lag, long-lived tokens with no refresh rotation, **no email verification and no
     password reset** (with the `psql`/`UPDATE` recovery path for a lost password spelled out in
     troubleshooting), and account deletion being immediate and irreversible with `/reports/*?format=csv` as
     the only export.
   - Multi-user reality check: registration is open to anyone holding the secret while
     `REGISTRATIONS_ALLOWED` is true, and every registered user is a full tenant with their own data. There is
     no admin, no quota, and no way to see another user's rows.

4. **`CLAUDE.md`.** Update the *Tenancy* section (title, `AccountId` → `UserId`, drop "v1 is
   single-account/multi-device: one seeded `Account` row"), the `Sync`/`Reports` paragraphs that name
   `AccountId`, the deployment section's `Account__Name` mention, and the plans index reference. Also fix the
   *Cross-repo* section's "Known drift as of this writing" paragraph, which still names pin `3e6e955` and lists
   `SyncItemResult.AuthoritativeOperation` as drift: the client re-vendored at `2065662` on 2026-08-19 (WL-100)
   and consumes `AuthoritativeOperation` as of WL-101, so that drift is closed and the paragraph now describes
   a problem that no longer exists. Replace it with the new pin this plan establishes. Add a short
   paragraph on the auth model: credentials are user-level, tokens are device-level, revocation is the
   security stamp plus the device-row check, and the cache TTL is the lag. Follow the file's existing
   convention — rules live as XML docs on the type that owns them, so link to them rather than restating.

5. **`scripts/smoke-test.ps1`.** It is the only test that exercises the deployment itself, and its current
   first step (`register-device`) no longer exists. Rework the sequence to: `GET /auth/capabilities` →
   `POST /auth/register` (a throwaway `smoke+<timestamp>@example.invalid` address) → `POST /auth/login` from
   device A → push a batch → login from device B → pull it → conflict → report → `GET /auth/devices` (asserts
   two) → `DELETE /auth/account`.
   - That last step makes the smoke test **self-cleaning**, which today's explicitly is not ("writes real rows
     and does not clean up"). It only ever deletes the throwaway user it created — update the `.DESCRIPTION`
     block, which currently promises the opposite.
   - Assert the interesting failures too, since only this script can see them in a real container: login with
     a wrong password → 401; registration with a wrong secret → 401. Keep the "first failure stops the run"
     behavior.
   - Add a `-BaseUrl`-compatible switch to skip the deletion (`-KeepAccount`) for anyone smoke-testing against
     a deployment they want to poke at afterwards.

6. **Client handoff (documentation only — do not edit the client repo).** Produce a
   `plans/S9-client-handoff.md` (or a clearly-marked section of this plan's PR description).

   **The job is narrower than it was when this plan was written.** The client is no longer waiting for a list
   of tickets to file — it has three written plans of its own (`plans/08-user-accounts-login-cutover.md`,
   `plans/09-auth-capabilities-and-account-ui.md`, `plans/10-account-management.md`, added 2026-08-26), each of
   which states that it is blocked on this handoff and that **the handoff is authoritative where the two
   disagree**. So hand those plans the facts they were derived from, and flag whatever has moved since. Cover:
   - **Re-vendor**: copy `src/WorkLedger.Contracts/` to client `Sync/Contracts/`, bump the provenance commit
     in `Sync/Contracts/README.md` (pinned at server `2065662` as of this writing), and note that
     `ReportDtos.cs` stays un-vendored by the client's own choice. This is client plan 08 item 1 / **WL-133**,
     and it blocks everything else on that side.
   - **The contract inventory from item 1, verbatim.** Client plan 08 item 1 carries its own copy of that table
     and explicitly says to verify it against this handoff rather than trust it. `AuthCapabilitiesResponse`'s
     six fields are the row most likely to be stale there.
   - **Which client plan covers what**, so nobody re-derives it:

     | Client plan | Covers | Server plans it consumes |
     |---|---|---|
     | `08-user-accounts-login-cutover.md` | re-vendor, `AppSettings`, `ApiSyncProvider` login, the 401/423/429 policy, `SyncCoordinator` messaging, the different-user database guard, the plain sign-in row | S6 (contract), S7 |
     | `09-auth-capabilities-and-account-ui.md` | the `GET /auth/capabilities` probe, registration and claim UI, rendering the auth state | S6 |
     | `10-account-management.md` | devices, change password, logout-all, delete account | S7 (`/auth/password`), S8 |

   - **Client files that must change** — still worth listing, because the handoff is what proves this side
     knows what it is breaking: `Sync/ApiSyncProvider.cs` (drop `register-device` and the
     `X-Bootstrap-Secret` header from the token path; add login; **do not** answer a 401 by re-authenticating
     — see the note below; the header comment listing depended-on server behaviors needs the revocation
     semantics added), `Services/SettingsService.cs` (`ApiToken`/`ApiTokenExpiresAtUtc` stay and gain an
     `ApiEmail`, but a token is now obtained by login — store the email, **never** the password; an OS
     credential store is a later option, re-prompting is v1), `Services/SyncCoordinator.cs` (its
     "Register this device first." / "Device registered." strings), `ViewModels/MainWindowViewModel.Sync.cs`
     and `Views/MainWindow.axaml` (a sign-in form; **hide the registration UI when `GET /auth/capabilities`
     reports `RegistrationEnabled=false`** — the requirement that motivated the flag), and
     `Tests/WorkLedger.Tests/SyncContractGuardTests.cs`.
   - **Behavioral notes the client needs**: a 401 mid-sync now means "your token was revoked or expired" and
     the right response is to route the user to a sign-in form, not to fail the sync silently; the same
     `DeviceId` under a different user is a different device; deleting a device from another machine logs this
     one out.
   - **One behavioral note going the other way — something *this* side should know.** The client will
     deliberately **not** re-authenticate automatically on a 401. Its reasoning (client plan 08 item 4,
     [WL-134](https://mnisan.atlassian.net/browse/WL-134)): a 401 from `/auth/login` is **deterministic** — the
     credential was wrong and the same credential will be wrong next time — so the correct automated retry
     count for a rejected credential is zero, not "a few, with backoff". The client therefore makes **at most
     one unattended failed login attempt per credential** and then discards it, which is exactly what stops a
     background sync on a 15-minute timer from locking a user out of their own server unattended.
     **The consequence to expect here: a client whose token has been revoked goes quiet and waits for a human.**
     "Devices stopped syncing after a security-stamp bump and stayed stopped until someone signed in again" is
     **intended behavior, not a client bug** — don't add a server-side accommodation for it, and don't read it
     as a sign that revocation is too aggressive.
   - **Tickets**: the client work is filed under epic **WL-105** as client-repo work (per `plans/README.md`).
     The list has grown past the five this plan originally named:

     | Ticket | Client plan | What |
     |---|---|---|
     | **WL-133** | 08 item 1 | re-vendor `WorkLedger.Contracts` — blocks the rest |
     | **WL-134** | 08 items 3–4 | `ApiSyncProvider` login and the 401/423/429 policy. Its summary was corrected from "treat 401 as re-authenticate" to "treat 401 as sign-in-required" — the original wording, taken literally, builds the lockout storm |
     | **WL-135** | 08 item 2 | `AppSettings`/`SettingsService`: the signed-in email and token, never the password |
     | **WL-136** | 08 item 7, 09 | login UI, since **split** into **WL-145** (the plain sign-in row, plan 08) and **WL-146** (capability-driven registration, claim, and revocation routing, plan 09) |
     | **WL-137** | 08 items 5, 8 | `SyncCoordinator` messaging and `SyncContractGuardTests` |
     | **WL-138** | 08 item 6 | guard the local database against signing in as a different user |
     | **WL-139** | 10 | account management — the client half of S8 — with subtasks **WL-140** (provider calls), **WL-141** (device list), **WL-142** (change password and logout-all), **WL-143** (delete account), **WL-144** (tests and docs) |

     Point the handoff at these rather than filing new ones.
   - **WL-138 deserves its own paragraph in the handoff**, because it is the one hazard this release creates
     that **nothing server-side can detect**. The client's SQLite database has no user column, so signing in as
     a different user against the same database pushes one user's rows into the other's account and pulls the
     other's rows into a database full of the first's. Every server-side check passes — the token is valid, the
     scope is right, the rows are well-formed, and the merge rules do exactly what they say. Under S2's single
     seeded account it was unreachable; after S6 it is one typo in a login form, and the guard is entirely the
     client's to build.

7. **`plans/README.md`.** Add S6–S9 to the index table with dependencies, extend the sequencing notes (S6 → S7
   → S8 are strictly linear; S9 last), and update the stale "Cross-repo contract handoff" paragraph, which
   still says the client "keeps its own stub DTOs" and hasn't implemented `ApiSyncProvider` — it has, and it
   vendors the real contracts.

8. **Release.** These four plans are one breaking release. Tag it as a minor bump with the `BREAKING CHANGE:`
   footers from S6/S7 (pre-1.0, so `feat!` is a minor, not a major) and write the release note around the
   upgrade path: **claim your account, then log every device in again**.

## Out of scope

- **Editing the client repo.** Handoff docs and tickets only.
- **Any new server behavior.** If S9 finds a gap in S6–S8, fix it in a follow-up commit on this branch and say
  so in the PR — but don't design new endpoints here.
- **Publishing `WorkLedger.Contracts` as a NuGet package.** It would remove the vendoring problem entirely and
  is the right long-term answer; it is also its own plan (S0 sketched the GitHub Packages step). Note it in
  the handoff as the follow-up that makes this class of cutover unnecessary.
- **CI changes**, beyond keeping the existing build/test workflow green.

## Acceptance criteria

- `WorkLedger.Contracts` compiles with no server-only dependencies and its final shape is inventoried in the
  handoff doc; the sync wire format is byte-for-byte what it was before S6.
- `docker compose up` on a **fresh** volume with a filled-in `.env` comes up, and the README's first-run
  commands work verbatim, copy-pasted.
- `docker compose up` on a volume holding **pre-S6 data** comes up with all data intact and `POST /auth/claim`
  takes ownership of it — the README upgrade section walked through end to end, not just written.
- `pwsh ./scripts/smoke-test.ps1 -Up` passes against the built image and leaves no throwaway account behind.
- `README.md`, `CLAUDE.md`, `.env.example` and `docker-compose.yml` contain no reference to
  `register-device`, `Account__Name`, `account_id` or "single v1 account".
- `plans/README.md` indexes S6–S9 and its cross-repo paragraph matches reality.
- The client handoff lists every client file that breaks with the re-vendor step first, points at client plans
  `08`/`09`/`10` by name rather than restating them, carries the current WL-105 ticket list (WL-133–139, plus
  WL-140–144 under WL-139 and WL-145/WL-146 under WL-136), and records the 401 behavior in **both**
  directions — the client routes a 401 to a sign-in form, and this side should expect a revoked client to go
  quiet until a human signs in.

## Suggested commit message

```
docs(auth): document user accounts, rework deployment config and the smoke test
```

(The smoke-test and `.env`/compose edits ride along; `docs:` is right for the release-please bump since
S6/S7 already carry the `feat!` commits.)

## Prompt for Claude Code

> Read `CLAUDE.md`, `plans/README.md`, and `plans/S6`–`S8` first, then read the client repo at
> `E:\GitHub-Repos\WorkLedger-Client` — its `plans/08-user-accounts-login-cutover.md`,
> `plans/09-auth-capabilities-and-account-ui.md` and `plans/10-account-management.md` first, then
> `Sync/Contracts/README.md`, `Sync/ApiSyncProvider.cs`, `Services/SettingsService.cs` and
> `Services/SyncCoordinator.cs` — **without modifying it**. Audit
> `src/WorkLedger.Contracts` against the client's vendored copy and write down the final inventory of added,
> removed and changed DTOs, confirming the sync wire format (PascalCase `SyncJson.Options`, the
> `SyncEntityTypes`/`SyncOperations` strings, positional push results) is unchanged since S6. Update
> `.env.example` and `docker-compose.yml`: drop `Account__Name`, add the `Auth__*` knobs and
> `REGISTRATIONS_ALLOWED`, and rewrite the `Bootstrap__Secret` and `Jwt__SigningKey` comments for their new
> meanings. Rewrite the affected parts of `README.md` — tenancy, first-run, the endpoint and config tables, the
> security section — and add an "Upgrading from a pre-user-accounts deployment" section covering backup,
> migrate, `POST /auth/claim`, re-login, and closing registration. Update `CLAUDE.md`'s tenancy, sync, reports
> and deployment sections and add a short auth-model paragraph. Rework `scripts/smoke-test.ps1` to register a
> throwaway account, log in from two devices, push/pull/conflict/report, list devices, and delete the account
> at the end (with a `-KeepAccount` switch), asserting a wrong password and a wrong signup secret both 401 —
> and fix the `.DESCRIPTION` block, which currently says it does not clean up. Write
> `plans/S9-client-handoff.md` listing the re-vendor step, the settled contract inventory (note that
> `AuthCapabilitiesResponse` now has **six** fields), every client file that breaks and why, and the
> behavioral notes in **both** directions: a 401 routes the user to a sign-in form and the registration UI
> hides when `/auth/capabilities` reports it disabled — and, going the other way, the client will **not**
> auto-retry a login after a 401, so a revoked device goes quiet until a human signs in, which is intended
> rather than a bug to accommodate here. Point the handoff at the client's own plans `08`, `09` and `10`
> (written already, and blocked on this document) and at the existing WL-105 tickets — WL-133–139, with
> WL-140–144 under WL-139 and WL-145/WL-146 under WL-136 — rather than filing new ones, and call out WL-138
> (signing in as a different user against the same local SQLite database) as the one hazard nothing
> server-side can detect. Add S6–S9 to `plans/README.md` and fix its stale cross-repo paragraph. Verify by
> bringing the stack up twice: once on a fresh volume following the README verbatim, and once on a volume
> seeded with pre-S6 data to prove the claim path.
