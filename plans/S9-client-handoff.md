# S9 client handoff — user accounts, login, and account management (WL-131)

**Server tickets:** [WL-109](https://mnisan.atlassian.net/browse/WL-109) / this document is
[WL-131](https://mnisan.atlassian.net/browse/WL-131), under epic
[WL-104](https://mnisan.atlassian.net/browse/WL-104).
**Client tickets:** epic [WL-105](https://mnisan.atlassian.net/browse/WL-105) — WL-133 through WL-139,
with WL-140–144 under WL-139 and WL-145/WL-146 under WL-136.

**Server state:** S6, S7 and S8 are implemented and on `main`; S9 (this cutover) is the branch carrying
this file. The last commit to touch `src/WorkLedger.Contracts/` is **`3d27c86`** (S8) — that is the pin
to record when re-vendoring.

## What this document is

The client's `plans/08-user-accounts-login-cutover.md`, `plans/09-auth-capabilities-and-account-ui.md`
and `plans/10-account-management.md` were written on 2026-08-26 against S6–S9 as **plans**, before any
of them was built. Each says it is blocked on this handoff and that **the handoff is authoritative where
the two disagree**. This is that document: the facts those plans were derived from, now that the server
half exists, plus what has moved since.

It deliberately does **not** restate those plans. Where a decision is the client's, it is named and
pointed at, not re-litigated here.

**Headline for the client: nothing in the three plans has been invalidated.** Every server-side
prerequisite they asked for landed (see [Prerequisites, all
settled](#prerequisites-from-client-plan-08-item-4½-all-settled)), and the contract is now frozen. The
one correction to make is a stale field count, in [the inventory](#the-contract-inventory).

## Step 0 — re-vendor `WorkLedger.Contracts` (WL-133). Nothing else can start.

Copy `src/WorkLedger.Contracts/` from this repo to the client's `Sync/Contracts/`, keeping the client's
two local conventions: the two-line provenance header on each file, and the explicit `using` directives
(the client does not enable `ImplicitUsings`; the server does). Then bump the pin in
`Sync/Contracts/README.md` from `2065662` to the SHA recorded above.

- **`Reports/ReportDtos.cs` stays un-vendored.** That is the client's own standing decision — it
  computes local reports and never calls `/reports/*` — and it is recorded in
  `Sync/Contracts/README.md`. It is not drift, and this handoff is not asking for it to change.
- The `Auth/` folder is where all the change is. `Sync/` and `Payloads/` are copy-and-replace with no
  behavioural consequence: see [the sync format](#the-sync-wire-format-is-unchanged-audited-not-assumed).
- Everything else in WL-105 depends on this, because the deleted types are referenced by
  `ApiSyncProvider` today and the added ones do not exist there yet. Land it as its own commit so the
  compiler errors that follow are attributable.

## The contract inventory

Audited file-by-file against the client's vendored copy at client `main` `3546d28` (WL-126). This is the
settled shape; the server does not intend to reopen it.

### `WorkLedger.Contracts.Auth` — added (8)

| Record | Shape |
|---|---|
| `RegisterUserRequest` | `(string Email, string Password, string? DisplayName)` — body of **both** `/auth/register` and `/auth/claim` |
| `UserSummary` | `(int Id, string Email, string? DisplayName, DateTime CreatedAtUtc)` |
| `AuthCapabilitiesResponse` | `(bool RegistrationEnabled, bool RegistrationRequiresSecret, int MinPasswordLength, bool HasUnclaimedAccount, int MaxFailedLogins, int LockoutMinutes)` |
| `LoginRequest` | `(string Email, string Password, string DeviceId, string? DeviceLabel)` |
| `LoginResponse` | `(string Token, DateTime ExpiresAtUtc, UserSummary User)` |
| `ChangePasswordRequest` | `(string CurrentPassword, string NewPassword)` |
| `DeviceSummary` | `(string DeviceId, string? Label, DateTime CreatedAtUtc, DateTime? LastSeenUtc, bool IsCurrent)` |
| `DeleteAccountRequest` | `(string Password, string ConfirmEmail)` |

> **`AuthCapabilitiesResponse` has SIX fields, not four.** Client plan 08 item 1 carries its own copy of
> this table and tells you to verify it here rather than trust it; this is the row most likely to be
> stale there. `MaxFailedLogins` and `LockoutMinutes` are [WL-147](https://mnisan.atlassian.net/browse/WL-147),
> added in S6 precisely so the record settles in one plan and the client vendors it once. Confirmed
> present in source, not inferred from the plan.
>
> Two things about those two fields, both stated on the record's own XML doc: `MaxFailedLogins` is
> **static policy, not a remaining-attempts count** (a remaining count could only be answered for an
> account that exists, which would enumerate accounts and undo login's uniform 401), and
> `LockoutMinutes` is **whole minutes rounded up** from a value the server holds as a fraction — the
> fraction is a test affordance and is deliberately not on the wire.

### Removed (2)

`RegisterDeviceRequest`, `RegisterDeviceResponse`. `POST /auth/register-device` no longer exists at all;
S7 deleted it. **Enrolment is now a side effect of logging in.**

### Changed (1)

`WhoAmIResponse`: `(int AccountId, string DeviceId)` → **`(int UserId, string Email, string? DisplayName,
string DeviceId)`**. `AccountId` → `UserId` is the breaking rename; the two added members mean a client
can render the signed-in identity from the one call it already makes.

### The sync wire format is unchanged — audited, not assumed

`Sync/SyncDtos.cs`, `Sync/SyncJson.cs` and `Payloads/EntityPayloads.cs` are byte-identical to the
vendored copy apart from the copy's own header and `using` lines. `Sync/SyncConstants.cs` differs in
**one XML doc word** (`(AccountId, SyncId)` → `(UserId, SyncId)`).

So, explicitly, for `SyncContractGuardTests` (WL-137): `SyncJson.Options` still emits **PascalCase**, the
`SyncEntityTypes` and `SyncOperations` string values are untouched, push results are still **positional**,
and `SyncItemResult` still carries `AuthoritativeOperation`. Nothing about push, pull, the merge rules or
the payload shapes changed in S6–S8. The only thing that changed is how you get a token.

## Which client plan covers what

| Client plan | Covers | Server plans it consumes |
|---|---|---|
| `08-user-accounts-login-cutover.md` | re-vendor, `AppSettings`, `ApiSyncProvider` login, the 401/423/429 policy, `SyncCoordinator` messaging, the different-user database guard, the plain sign-in row | S6 (contract), S7 |
| `09-auth-capabilities-and-account-ui.md` | the `GET /auth/capabilities` probe, registration and claim UI, rendering the auth state | S6 |
| `10-account-management.md` | devices, change password, logout-all, delete account | S7 (`/auth/password`), S8 |

## The endpoints, and the status codes the client has to distinguish

The client's 401/423/429 policy (WL-134) hangs off these, so they are listed rather than left to be
discovered:

| Endpoint | Auth | Answers |
|---|---|---|
| `GET /auth/capabilities` | anonymous | `200`. Never fails for policy reasons — it *is* the policy. |
| `POST /auth/register` | `X-Bootstrap-Secret` | `201` + `UserSummary`; `403` registration disabled, `401` bad secret, `400` bad email/password, `409` email taken, `429` rate-limited |
| `POST /auth/claim` | `X-Bootstrap-Secret` | `200` + `UserSummary`; `401` bad secret, `400` bad input, `409` nothing claimable, `429`. **Not** gated by `RegistrationEnabled`. |
| `POST /auth/login` | credentials in body | `200` + `LoginResponse`; `401` **for every bad credential**, `423` + `Retry-After` locked out, `429` + `Retry-After` rate-limited |
| `GET /auth/whoami` | bearer | `200` + `WhoAmIResponse`; `401` if the token has been revoked |
| `POST /auth/password` | bearer | `200` + `LoginResponse` (**a new token — store it**); `401` current password wrong, `400` new password rejected |
| `GET /auth/devices` | bearer | `200` + `DeviceSummary[]` |
| `DELETE /auth/devices/{deviceId}` | bearer | `204`; `404` for an id this user does not own |
| `POST /auth/logout-all` | bearer | `200` + `LoginResponse` (**a new token — store it**) |
| `DELETE /auth/account` | bearer | `204`; `401` password wrong, `400` `ConfirmEmail` mismatch |

Three of those shapes are load-bearing:

- **Registration returns no token.** Tokens are device-scoped and only `/auth/login` mints one. A
  registration flow that expects to be signed in afterwards has to follow it with a login.
- **`/auth/password` and `/auth/logout-all` return a `LoginResponse`.** Both bump the security stamp,
  which kills every outstanding token *including the caller's own*; the replacement token in the
  response is what stops that from looking like a bug. Dropping it signs the user out of the device they
  are sitting at. (Client plan 10 item 1 already knows this.)
- **`{deviceId}` in the delete route is the client's own per-install GUID string** — the same value sent
  as `LoginRequest.DeviceId` and `SyncChangeDto.OriginDeviceId`. The server's internal row id is never
  on the wire, exactly as synced entities are addressed by `SyncId`.

## Client files that must change, and why

Listed because a handoff that cannot name what it breaks has not really been given. Verified against
client `main` `3546d28`.

| File | What breaks / what is needed |
|---|---|
| `Sync/ApiSyncProvider.cs` | Compile errors: `RegisterDeviceRequest`/`Response` are gone. `TryBootstrapAsync()` and its `auth/register-device` POST become a login call; `BootstrapSecretHeader` and `_bootstrapSecretAccessor` leave the token path entirely (the secret's only remaining use is registration/claim, plan 09). **Do not answer a 401 by re-authenticating** — see [the note going the other way](#one-note-going-the-other-way--what-this-side-should-expect). The header comment listing depended-on server behaviours should gain the revocation semantics: a token can now die mid-session for reasons that have nothing to do with expiry. |
| `Sync/ApiSyncProvider.cs` — `TokenRenewalMargin` | **This one is easy to miss.** It currently re-bootstraps 5 minutes before expiry so a token never dies mid-batch. That worked because bootstrapping needed only a secret the app could hold. Renewal now needs a *password*, which WL-135 says never to store — so proactive renewal cannot be silent any more. It becomes "warn/prompt ahead of expiry", or it goes away and expiry is handled like any other 401. Either is fine; silently keeping it is not, because there is nothing left for it to call. |
| `Services/SettingsService.cs` | `ApiToken` and `ApiTokenExpiresAtUtc` stay but change provenance — their comments name `POST /auth/register-device` and `RegisterDeviceResponse.ExpiresAtUtc`, both gone. Add `ApiEmail` (WL-135). **Never store the password.** Re-prompting is v1; an OS credential store is a later option, not this release. |
| `Services/SyncCoordinator.cs` | `RegisterDeviceAsync(string bootstrapSecret)` becomes a sign-in call. The user-facing strings are wrong in a way users will read: `"Register this device first."` (in `DescribeConfigurationProblem`) and `"Device registered. Token valid until …"`. WL-137 also wants "no" to be distinguishable — signed out, session expired, wrong credential, locked out, rate-limited are five different messages. |
| `ViewModels/MainWindowViewModel.Sync.cs` | The `BootstrapSecret` property and the comment at its declaration describe it as the gate on `POST /auth/register-device`. Needs the sign-in row (WL-145) and the capability-driven auth state (WL-146). |
| `Views/MainWindow.axaml` | The bootstrap-secret `TextBox` and the `"Register this device"` button in the API sync panel. **Hide the registration UI when `GET /auth/capabilities` reports `RegistrationEnabled=false`** — that flag exists for this exact requirement, so a closed server shows no form that can only end in a `403`. |
| `Tests/WorkLedger.Tests/ApiSyncProviderTests.cs` | Asserts `auth/register-device` URLs and carries `BootstrapSecret` on its `RecordedRequest`. Several tests are about bootstrapping a token and have no meaning after the cutover. |
| `Tests/WorkLedger.Tests/SyncContractGuardTests.cs` | The guard against the vendored copy. Sync-side assertions should still pass unchanged — if they don't, something moved that this handoff says didn't, and that is worth a bug on the server side rather than a fix on the client's. |

## Behavioural notes the client needs

1. **A 401 mid-sync now means "your session is gone", not "something went wrong".** Tokens can be
   revoked: a device removed from another machine, a password changed, a sign-out-everywhere, or the
   account deleted. The right response is to route the user to a sign-in form — not to fail the sync
   silently, and not to retry. (Client plan 09 item 6.)
2. **The same `DeviceId` under a different user is a different device.** Device rows are keyed by
   `(UserId, DeviceId)`. Signing in as someone else does not inherit the old device row, and it does not
   inherit its data either.
3. **Deleting a device from another machine signs this one out.** Immediately, in the normal case: the
   endpoints that revoke evict the server's revocation cache themselves, so `Auth:StampCacheSeconds` (30
   by default) is the lag only when a revocation happens by some other route.
4. **Removing a device does not remove its data, and that is correct.** Projects, tasks and entries
   belong to the user; `OriginDeviceId` is provenance, not ownership. A device list is not a data
   manager, and the UI should not imply it is. (Client plan 10 item 2 has this right.)
5. **Login answers every failure with the same `401`** — unknown email, wrong password, and a
   credential-less legacy account alike. The client cannot distinguish "no such account" from "wrong
   password", and should not try to; registration's `409` on a duplicate email is not a precedent,
   because getting that far required the signup secret.
6. **Repeated failures produce a `423` with a `Retry-After`, not another `401`.** The lockout unlocks
   itself and the failure counter resets when the window expires, so there is nothing for a user to ask
   an operator for. A `429` (also with `Retry-After`) is the per-IP rate limit on the credential
   endpoints and is a different thing from a lockout — worth saying differently in the UI.
7. **Account deletion is immediate and total on the server, and touches nothing locally.** After a
   `204`, every token for that account is dead and the local SQLite database is untouched and complete.
   Whatever the client says next should not imply the local data is gone. (Client plan 10 items 3–4.)

## One note going the other way — what *this* side should expect

The client will deliberately **not** re-authenticate automatically after a 401
([WL-134](https://mnisan.atlassian.net/browse/WL-134), client plan 08 item 4). Its reasoning, which this
side accepts: a 401 from `/auth/login` is **deterministic** — the credential was wrong and the same
credential will be wrong next time — so the correct automated retry count for a rejected credential is
zero, not "a few, with backoff". The client therefore makes at most **one unattended failed login
attempt per credential** and then discards it. That is what stops a background sync on a 15-minute timer
from locking a user out of their own server while they are asleep.

**The consequence to expect here: a client whose token has been revoked goes quiet and waits for a
human.** "Devices stopped syncing after a security-stamp bump and stayed stopped until someone signed in
again" is **intended behaviour, not a client bug.** Do not add a server-side accommodation for it, and
do not read it as evidence that revocation is too aggressive.

WL-134's Jira summary was corrected during client planning from "treat 401 as re-authenticate" to "treat
401 as sign-in-required": the original wording, taken literally, builds the lockout storm.

## WL-138 — the one hazard nothing server-side can detect

[WL-138](https://mnisan.atlassian.net/browse/WL-138) (client plan 08 item 6) deserves its own section,
because it is the hazard this release creates and the **only** one the server cannot see.

The client's SQLite database has no user column. Sign in as a different user against the same local
database and the sync loop will push one user's rows into the other's account, and pull the other's rows
into a database already full of the first's. **Every server-side check passes**: the token is valid, the
scope is right, the rows are well-formed, and last-write-wins does exactly what it documents. There is
no query the server could add that would notice, because from its side nothing is wrong — a device
belonging to user B pushed some rows, which is precisely what devices do.

Under S2's single seeded account this was unreachable: there was one account, so "a different user" did
not exist. After S6 it is one typo in a login form. **The guard is entirely the client's to build** —
stamp the local database with its owner and refuse a mismatch. Nothing here will catch it, and nothing
here will warn about it.

## Prerequisites from client plan 08 item 4½ — all settled

That plan raised three server-side items and asked for them to be settled before S7 was built. All three
landed, so no client plan needs revising on their account:

| Asked for | State |
|---|---|
| `FailedLoginCount` must reset when the lockout window expires, not only on a successful login ([WL-118](https://mnisan.atlassian.net/browse/WL-118)) | **Done.** Stated in full on `AuthEndpoints`' login doc and covered by tests, which is why `AuthOptions.LockoutMinutes` binds as a fraction — a test has to live through a real window. |
| A `423` must neither increment the counter nor extend `LockoutEndUtc` (WL-118) | **Done.** A `423` writes nothing at all. Together with the reset above, this is what makes it impossible to hold an account locked by hammering it. |
| Add `MaxFailedLogins` and `LockoutMinutes` to `AuthCapabilitiesResponse` ([WL-147](https://mnisan.atlassian.net/browse/WL-147)) | **Done in S6** — hence six fields. Client plan 09 can warn truthfully before an attempt that would lock the account. |

One more, unasked but worth knowing: the advertised policy and the enforced policy come from the same
`AuthOptions` accessor. A server that advertises 5/15 and enforces something else is not a state this
code can reach.

## Tickets — point at these, do not file new ones

| Ticket | Client plan | What |
|---|---|---|
| [WL-133](https://mnisan.atlassian.net/browse/WL-133) | 08 item 1 | Re-vendor `WorkLedger.Contracts` — **blocks the rest** |
| [WL-134](https://mnisan.atlassian.net/browse/WL-134) | 08 items 3–4 | `ApiSyncProvider` login and the 401/423/429 policy |
| [WL-135](https://mnisan.atlassian.net/browse/WL-135) | 08 item 2 | `AppSettings`/`SettingsService`: the signed-in email and token, never the password |
| [WL-136](https://mnisan.atlassian.net/browse/WL-136) | 08 item 7, 09 | Login UI — split into [WL-145](https://mnisan.atlassian.net/browse/WL-145) (the plain sign-in row, plan 08) and [WL-146](https://mnisan.atlassian.net/browse/WL-146) (capability-driven registration, claim, and revocation routing, plan 09) |
| [WL-137](https://mnisan.atlassian.net/browse/WL-137) | 08 items 5, 8 | `SyncCoordinator` messaging and `SyncContractGuardTests` |
| [WL-138](https://mnisan.atlassian.net/browse/WL-138) | 08 item 6 | Guard the local database against signing in as a different user |
| [WL-139](https://mnisan.atlassian.net/browse/WL-139) | 10 | Account management — the client half of S8 — with subtasks [WL-140](https://mnisan.atlassian.net/browse/WL-140) (provider calls), [WL-141](https://mnisan.atlassian.net/browse/WL-141) (device list), [WL-142](https://mnisan.atlassian.net/browse/WL-142) (change password and logout-all), [WL-143](https://mnisan.atlassian.net/browse/WL-143) (delete account), [WL-144](https://mnisan.atlassian.net/browse/WL-144) (tests and docs) |

## The follow-up that makes this class of cutover unnecessary

Publish `WorkLedger.Contracts` as a package (S0 sketched the GitHub Packages step). The vendored copy
exists only because this repo publishes nothing and a cross-repo `ProjectReference` fails in the client's
CI, which checks out one repo. With a package, "the copy fell behind" stops being a thing that can
happen silently, and `Sync/Contracts/README.md` already says the namespaces are upstream's verbatim so
nothing else has to change. It is its own plan, not part of this one.

## What S9 did not do

- **It did not touch the client repo.** Per `plans/README.md`, server work reads the client, coordinates,
  and lands client edits as client-repo work. The re-vendor is WL-133, on that side.
- **It added no server behaviour.** No new endpoints, no changed status codes, no contract edits. If
  something in this document disagrees with the server's code, the code is right and this is a bug.
