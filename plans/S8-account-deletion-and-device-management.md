# Plan S8 — Account deletion, device management, logout-all (WL-108)

**Tickets:** [WL-108](https://mnisan.atlassian.net/browse/WL-108), under epic [WL-104](https://mnisan.atlassian.net/browse/WL-104) (*User accounts: user-owned tenancy, login, and device management (server)*).
Subtasks: scope 1 → WL-121, 2 → WL-122, 3 → WL-123, 4–5 → WL-124, 6 (tests) → WL-125. The client-side work this release implies is tracked separately under epic [WL-105](https://mnisan.atlassian.net/browse/WL-105).

## Context

S6 created user accounts; S7 made them loggable-into and gave outstanding tokens a kill switch (the security
stamp, plus the device-row existence check in `OnTokenValidated`). S8 spends that mechanism: the endpoints a
user needs to see what is attached to their account, detach a device, and delete the whole account.

Deletion is the endpoint that has to be right the first time. Getting it wrong in the safe direction leaves
orphaned rows that still answer to a token; getting it wrong in the unsafe direction destroys data that no
backup covers, because on a self-hosted box the user *is* the operator. So: **re-authentication before
anything is dropped, one transaction, and everything goes** — devices, projects, tasks, time entries, user.

Hard delete, not soft. A soft-delete flag would mean every sync and report query grows an
`AND NotDeleted` term for a case that only arises once per account, and "your data is still on the server but
we promise not to look" is not what someone asking to delete their account means. A grace period is the
feature worth wanting here, and it is a deliberate deferral — noted in Out of scope.

## Scope

1. **`GET /auth/devices`** (authenticated) → `DeviceSummary[]`:
   `(string DeviceId, string? Label, DateTime CreatedAtUtc, DateTime? LastSeenUtc, bool IsCurrent)`.
   Scoped to the token's user, ordered by `LastSeenUtc` descending. **`DeviceId` — the client's GUID string —
   is the public identifier, never the internal `int Id`**, consistent with the SyncId rule in `CLAUDE.md`.
   `IsCurrent` compares against `ICurrentContext.DeviceId` so the UI can label "this device".

2. **`DELETE /auth/devices/{deviceId}`** (authenticated). Deletes that device row **within the token's user
   only** — an unknown or other-user `deviceId` is a `404`, same as the report routes do for foreign SyncIds.
   Returns `204`.
   - Revocation is automatic: S7's `OnTokenValidated` fails any token whose device row is gone. **Evict that
     device's cache key immediately** so the kill doesn't wait out `Auth:StampCacheSeconds`.
   - Deleting your *own* current device is allowed and means "log this device out". Say so in the XML doc;
     it's a surprising-but-correct behavior that someone will otherwise "fix" with a guard.
   - **The device's synced rows are not touched.** Projects/tasks/entries belong to the user, not the device;
     `OriginDeviceId` on those rows is provenance, and deleting the rows a laptop happened to create would
     destroy shared history. Document this — it is the single most plausible wrong assumption about this
     endpoint. The removed device re-enrolls simply by logging in again.

3. **`POST /auth/logout-all`** (authenticated). Bumps the user's `SecurityStamp`, evicts the cache, and returns
   `200` with a freshly minted token for the **calling** device (same pattern as `POST /auth/password` in S7) —
   otherwise the caller logs itself out along with everyone else. Device rows survive; only tokens die.

4. **`DELETE /auth/account`** (authenticated). Body
   `DeleteAccountRequest(string Password, string ConfirmEmail)`.
   - **Re-authenticate**: verify `Password` against the stored hash → `401` on mismatch. A leaked token must
     not be sufficient to destroy an account.
   - **Confirm intent**: `ConfirmEmail` must match the account's email case-insensitively → `400` otherwise.
     This is the typed-confirmation gesture, and it also stops a client bug from deleting the wrong account.
   - **Delete in one transaction**, children first so the FKs never complain regardless of their configured
     delete behavior: `TimeEntries` → `TaskItems` → `Projects` → `Devices` → `User`, each scoped
     `WHERE UserId = <token user>`. Use `ExecuteDeleteAsync` per table (one statement each, no entity
     materialization) inside an explicit `BeginTransactionAsync`.
   - Return `204`. The caller's token is dead the moment the user row is gone — evict its cache key on the way
     out so that is immediate rather than up to `Auth:StampCacheSeconds` later.
   - **Log one warning line**: user id and the per-table row counts. Not the email — the request log
     deliberately records method/path/status only, and this is the one place tempted to widen that.

5. **Concurrency.** A sync push racing an account deletion must not resurrect rows. The transaction plus the
   `OnTokenValidated` user check make the window small but nonzero (a push admitted just before the delete
   commits). Handle the honest way: sync writes are already inside their own transaction and will either
   commit before the delete's `ExecuteDelete` or fail on the missing FK parent → the endpoint returns a `500`
   at worst for a user who no longer exists. Add a test that pushes concurrently with a delete and asserts the
   database ends **empty for that user** either way; if it proves flaky, take a `FOR UPDATE` lock on the user
   row at the top of the delete and note why.

6. **Tests:**
   - `GET /auth/devices` lists only the caller's devices (seed a second user with devices and assert they are
     absent) and flags `IsCurrent` correctly.
   - `DELETE /auth/devices/{id}`: 204 and the row is gone; that device's token is refused on the next
     `/sync/pull` **immediately**, not after the cache TTL; another user's device id → 404 and the row
     survives; deleting your own current device → 204 then 401.
   - Deleting a device leaves the user's projects/tasks/entries intact, and the device can log in again.
   - `POST /auth/logout-all`: the returned token works, a token from a second device does not.
   - `DELETE /auth/account`: wrong password → 401 and **nothing** is deleted; mismatched `ConfirmEmail` → 400
     and nothing is deleted; happy path → 204, every table has zero rows for that user, the token is refused,
     and **a second user's data is untouched** (the test that actually proves the scoping).
   - Deletion of an account with a meaningful amount of data (a few hundred entries) completes and leaves no
     orphans in any of the four child tables.

## Out of scope

- **Grace-period / undo deletion.** Immediate and irreversible in v1. The follow-up worth having is a
  `DeletedAt` soft-delete plus a purge job; note it as such rather than half-building it.
- **Data export before deletion.** A "download my data" endpoint is a real companion to this and a separate
  plan; `/reports/*?format=csv` is the closest thing today. Mention it in the README (S9) so the answer to
  "can I keep a copy?" isn't nothing.
- **Admin/operator endpoints** — listing all users, deleting someone else's account. There is no admin role in
  this model; an operator with shell access has `psql`.
- **Renaming a device, editing the display name, changing the account email.** Small and additive; not needed
  to close the account-lifecycle story.
- **Client changes and doc/deployment updates** — S9.

## Acceptance criteria

- A user can list their devices, remove one, and log every device out, all scoped to their own token.
- Removing a device revokes its token immediately and leaves the user's synced data intact.
- `DELETE /auth/account` requires both the current password and the typed email; either failing leaves the
  account fully intact.
- A successful deletion removes every row the user owns across all five tables in one transaction and leaves
  other users untouched.
- No endpoint added here accepts a user identifier from the body or URL — scope still comes only from the
  token.
- Full suite green under Testcontainers.

## Suggested commit message

```
feat(auth): add account deletion, device management and logout-all
```

## Prompt for Claude Code

> Read `CLAUDE.md` and `plans/S7-login-and-device-enrollment.md` first. Add four authenticated endpoints, all
> scoped to the token's user and none accepting a user id from the request: `GET /auth/devices` returning
> `DeviceSummary` records keyed on the client's `DeviceId` string (never the internal int id) with an
> `IsCurrent` flag; `DELETE /auth/devices/{deviceId}` returning 204, 404 for an unknown or other-user device,
> evicting that device's security-stamp cache entry so its token dies immediately, and explicitly **not**
> deleting the projects/tasks/entries that device originated (document that); `POST /auth/logout-all` which
> bumps the user's `SecurityStamp`, evicts the cache and returns a fresh token for the calling device; and
> `DELETE /auth/account` which verifies the supplied current password (401) and a typed `ConfirmEmail` matching
> the account (400), then deletes `TimeEntries`, `TaskItems`, `Projects`, `Devices` and the `User` row —
> children first, `ExecuteDeleteAsync` per table, all inside one explicit transaction — returns 204, evicts the
> cache, and logs a single warning with the user id and per-table counts but not the email. Write
> Testcontainers tests proving: device listing and deletion are user-scoped (seed a second user and assert its
> rows survive everything), a removed device's token is refused on the very next request, deleting a device
> keeps the synced data, both deletion guards leave the account fully intact when they fail, a successful
> deletion empties all five tables for that user, and a push racing a delete cannot leave orphaned rows. Leave
> the client repo, `.env.example`, `docker-compose.yml`, `README.md` and `scripts/smoke-test.ps1` to S9.
