# Plan S3 — Sync Endpoints (push / pull + last-write-wins) — WL-76

| Subtask | Scope |
|---------|-------|
| [WL-82](https://mnisan.atlassian.net/browse/WL-82) | Implement `POST /sync/push` (LWW reconciliation) |
| [WL-83](https://mnisan.atlassian.net/browse/WL-83) | Implement `GET /sync/pull` + payload (de)serialization & validation |
| [WL-84](https://mnisan.atlassian.net/browse/WL-84) | Sync tests (Testcontainers) |

## Context

This is the heart of the backend and the endpoint the client's `ApiSyncProvider` (client Plan 6) will be
built against. It implements the two-endpoint contract documented on the client side and in the README:
- `POST /sync/push` — client sends a batch of queued changes; server reconciles with last-write-wins and
  returns per-item outcomes + authoritative versions.
- `GET /sync/pull?since=<ISO8601>` — server returns this account's changes newer than `since` from **other**
  devices, so the caller converges.

The server is **authoritative** (README: "authoritative datastore"). It applies the same **last-write-wins**
rule the client's `SyncOrchestrator` uses, so both sides resolve conflicts identically. **The exact rule is
stated in "Reconciliation rules" below — it was reconciled against the client's real code on 2026-08-11 and
is *not* what the first draft of this plan assumed.**

Hard rules inherited from S1 and the client addenda:
- **Match rows by `(AccountId, SyncId)`, never internal `Id`.**
- **Deletes are tombstones** (`IsDeleted = true` + version bump), so they propagate on pull.
- **Server stamps `UpdatedAt`** — already implemented in `WorkLedgerDbContext.StampUpdatedAt()`
  (`src/WorkLedger.Server/Data/WorkLedgerDbContext.cs:78`); the pull cursor is server time, never client time.
- **Scope everything to the token's `AccountId`** (S2) — a device can only ever push/pull its own account's
  data. This is the multi-tenant safety boundary; a missing `WHERE AccountId = ?` here is a data-leak bug.

## What already exists (don't rebuild it)

S0–S2 have landed on `main`; S3 is additive on top of them:
- **Contracts** — `SyncChangeDto`, `SyncPushRequest`, `SyncItemResult`, `SyncPushResponse`, `SyncPullResponse`
  (`src/WorkLedger.Contracts/Sync/SyncDtos.cs`) and `ProjectPayload` / `TaskItemPayload` / `TimeEntryPayload`
  (`src/WorkLedger.Contracts/Payloads/EntityPayloads.cs`). Use these as-is; extend only as noted in Scope 1.
- **Entities** — `Project` / `TaskItem` / `TimeEntry` all implement `IVersionedSyncEntity`
  (`SyncId`, `Version`, `OriginDeviceId`, `IsDeleted`, `UpdatedAt`), with `AccountId` on every row.
- **Indexes** — `(AccountId, SyncId)` unique and `(AccountId, UpdatedAt)` already exist for exactly the
  lookup and cursor queries this plan issues; `(AccountId, ProjectSyncId)` / `(AccountId, TaskSyncId)` exist
  for S4. No new migration should be needed unless something below adds a column.
- **Auth** — `ICurrentContext` gives `(AccountId, DeviceId)` from the token (`src/WorkLedger.Server/Auth/CurrentContext.cs`),
  and `Program.cs` sets an authorization **fallback policy**, so a newly mapped endpoint is protected by
  default. Do *not* add `.AllowAnonymous()` to the sync endpoints.

## Client vs server: who does what

The client repo (`WorkLedger-Client`) is **not modified by this plan**, but the server's behavior is
constrained by what the client already does. Verified against client `main` @ `7ae7565` on 2026-08-11:

| Concern | Client (today) | Server (this plan) |
|---|---|---|
| Change capture | `LocalDbService` writes a `SyncQueue` row per mutation, payload = the serialized entity | — |
| Version stamping | `StampForInsert` → `Version = 1`; `StampForUpdate` → `Version++`, both stamp `OriginDeviceId` | Never invents a version except on tombstones (see rule D) |
| `Version`/`OriginDeviceId` on the wire | **Not columns on `SyncQueue`** — they live inside `PayloadJson`; `FileSyncProvider.ToWireEntry` lifts them to the wire top level (`Sync/FileSyncProvider.cs:130`). `ApiSyncProvider` must do the same | Trusts the **top-level** `SyncChangeDto.Version` / `OriginDeviceId` for LWW; does not dig into the payload |
| Conflict resolution | `RemoteWins(remote, local) => remote > (local ?? 0)` (`Services/SyncOrchestrator.cs:177`) | Same comparison, mirrored (rules A–D) |
| Self-echo | Skips changes whose `OriginDeviceId == DeviceId` before comparing (`Services/SyncOrchestrator.cs:104`) | Excludes the caller's own `OriginDeviceId` from pull; tolerates self-echo on push idempotently |
| Pull cursor | `_lastSyncedAt` is an **in-memory client-clock** value that resets to `DateTime.MinValue` on restart (`Services/SyncOrchestrator.cs:38,78,83`) | Returns `ServerTime` for the client to adopt; must stay correct when `since` is skewed or `MinValue` (gap **G2**) |
| Push ack | `SyncResult.AcceptedSyncQueueIds` — **local `SyncQueue.Id` ints** | Returns one `SyncItemResult` per change, **in request order** (rule F) — the client maps positionally (gap **G4**) |
| Retry/backoff | Client owns the queue; unaccepted rows stay `IsSynced = false` and retry | Stateless request/response; no retry, no queue |
| Payload JSON casing | Serializes and deserializes with **default** `JsonSerializer` options → PascalCase, case-**sensitive** on read (`Services/LocalDbService.cs:140`, `Services/SyncOrchestrator.cs:181`) | Must emit PascalCase; accepts case-insensitively (rule G) |

## Reconciliation rules (normative — put these in code comments)

Let `incoming` be a `SyncChangeDto` and `existing` the row matched by `(AccountId, EntityId)`.

- **A. Higher version wins.** Accept and apply when `incoming.Version > existing.Version`.
- **B. Equal or lower version loses — *from a different device*.** The server keeps its row, writes nothing,
  and returns `Accepted = false` with `AuthoritativeVersion` + `AuthoritativePayloadJson` so the client can
  adopt the winner immediately. **No `CreatedAt` tie-break** — the earlier draft of this plan specified one,
  but the client cannot implement it: `RemoteChange` (`Sync/RemoteChange.cs`) has no `CreatedAt` field at all,
  and `RemoteWins` is a bare `>` comparison. A server-side timestamp tie-break would make the two sides
  disagree, which is the exact failure mode this rule exists to prevent. `SyncChangeDto.CreatedAt` is still
  carried and stored — it is just not an input to the decision.

  **The client must retire a queue row on `Accepted = false`, not retry it.** The server's answer is final —
  re-pushing produces the same rejection forever, and today's `SyncOrchestrator` leaves anything not in
  `AcceptedSyncQueueIds` pending. Adopting `AuthoritativePayloadJson` *and* dropping the queue row is the
  correct client response; that is WL-94's half of this contract.
- **C. No existing row → insert.** Deserialize `PayloadJson` into the Contracts payload DTO, map to typed
  columns, set `AccountId` from the token, take `Version` / `OriginDeviceId` / `CreatedAt` from the change.
- **D. Deletes tombstone, and the server bumps the version.** A `"delete"` is accepted when
  `incoming.Version >= existing.Version` (note `>=`, not `>`), sets `IsDeleted = true`, and stores
  `Version = existing.Version + 1`. Two independent client facts force this:
  1. `LocalDbService.DeleteProjectAsync` / `DeleteTaskAsync` / `DeleteTimeEntryAsync`
     (`Services/LocalDbService.cs:197`, `:238`, `:276`) queue the delete with the entity **as-is** — they never
     call `StampForUpdate`, so a delete arrives carrying the *same* version as the last update. Under a strict
     `>` rule the server would reject every delete it ever received.
  2. The receiving client applies remote changes with the same strict `>`, so a tombstone echoed back at the
     original version would be dropped by the *other* device. Bumping makes the tombstone strictly beat every
     other device's copy, so the delete actually lands.

  Return the bumped value as `AuthoritativeVersion` with `Accepted = true`. A delete for a `SyncId` with **no**
  existing row inserts a tombstone row (`IsDeleted = true`) so the delete still propagates to devices that
  received the create from a third device. This is a deliberate, narrow deviation from strict LWW; comment it
  with the reason, and see gap **G3** for the client-side fix that would let it be removed.

  Two corollaries worth stating, since both are observable: the delete's **payload is applied best-effort**
  (the client sends the entity as the delete's payload, and we only reach this branch when that payload is at
  least as new as the row, so it leaves the tombstone's columns at the last state anyone saw — but a missing
  or unusable payload still tombstones). And a **winning update revives a tombstone** (`IsDeleted = false`):
  LWW applied consistently means "edited after someone else deleted it" resolves to alive.
- **E. Idempotent replay — the same-device carve-out to rule B.** When the losing change comes from the
  **same device that authored the server's row** (`existing.OriginDeviceId == incoming.OriginDeviceId` and
  `incoming.Version <= existing.Version`), the change is already reflected: it is a replay, a self-echo, or an
  ack the client never received. Return `Accepted = true` so the client retires the queue row. Calling it a
  conflict instead would leave that row pending and re-pushed on every sync, forever — a permanent queue leak
  from a successful write.

  Either way, **do not touch the row at all** — not merely "don't bump the version". Any EF write restamps
  `UpdatedAt`, and `UpdatedAt` is the pull cursor, so an incidental write re-broadcasts the row to every other
  device on their next pull. A no-op write is not free here; it is a broadcast.

  A replayed *delete* against an already-tombstoned row (`existing.IsDeleted && incoming.Version <=
  existing.Version`) is likewise an accepted no-op, so a retried batch cannot ratchet the version upward.
- **F. Results are positional.** Return exactly one `SyncItemResult` per input change, in request order.
  `SyncItemResult.EntityId` alone is not a key: a single batch legitimately contains several queue rows for
  the same entity (create → update → delete), and the client's ack list is keyed by local `SyncQueue.Id`,
  which the server never sees. Document the positional contract on the endpoint.
- **G. One JSON policy.** Use a single shared options instance (e.g. `SyncJson.Options`) for **all** payload
  (de)serialization: default (**PascalCase**) naming for output, `PropertyNameCaseInsensitive = true` for
  input. Do **not** use `JsonSerializerDefaults.Web` for payloads — it emits camelCase, and the client reads
  payloads with `JsonSerializer.Deserialize<T>(payloadJson)` using default, case-sensitive options
  (`Services/SyncOrchestrator.cs:181`), so a camelCase payload would deserialize into an all-defaults entity
  **silently**, with no exception. This is a data-corruption trap, not a style preference.
- **H. UTC normalization.** Normalize every incoming `DateTime` (`CreatedAt`, `UpdatedAt`, `StartTime`,
  `EndTime`, and the `since` argument) to `DateTimeKind.Utc` before it reaches EF —
  `Unspecified` → `SpecifyKind(Utc)`, `Local` → `ToUniversalTime()`. The columns are `timestamptz` and Npgsql
  throws on a non-UTC `DateTime`; the client stores times through SQLite, which does not preserve `Kind`, so
  `Unspecified` values on the wire are the expected case, not an edge case.

## Scope

1. **Contracts additions (small).** Add shared constants so neither side hand-writes the magic strings:
   `SyncEntityTypes` = `"project"` / `"task"` / `"timeentry"` and `SyncOperations` = `"create"` / `"update"` /
   `"delete"` — these values are fixed by the client's `LocalDbService.EntityTypes` / `SyncOperations`
   (`Services/LocalDbService.cs:16-27`); do not rename them. Add the shared `SyncJson.Options` instance (rule G)
   here too, so the client picks up the same policy when it consumes the package.

2. **`POST /sync/push`** (WL-82, authorized, account-scoped). Body: `SyncPushRequest`.
   - **Order the batch before applying**: stable sort by entity type rank (project → task → time entry),
     preserving the original relative order *within* a type. Stability matters: a batch may contain
     create → update → delete for one entity, and reordering those inverts the outcome. Ordering by type keeps
     parents ahead of children so `ProjectSyncId` / `TaskSyncId` references resolve (links are by SyncId, so a
     missing parent is not a hard FK violation, but ordering keeps the data sane and S4 reports correct).
   - Apply rules A–F per item against `(AccountId, EntityId)`.
   - Wrap the whole batch in a **transaction** so a mid-batch failure doesn't half-apply.
   - Return `SyncPushResponse { Results, ServerTime = UtcNow }`.

3. **`GET /sync/pull?since=<ISO8601>`** (WL-83, authorized, account-scoped).
   - Query each synced entity `WHERE AccountId = @acct AND UpdatedAt > @since AND OriginDeviceId <> @callerDevice`
     — including tombstoned rows so deletes propagate. Covered by the existing `(AccountId, UpdatedAt)` index.
   - Parse `since` with `DateTimeStyles.AdjustToUniversal | AssumeUniversal`. Missing/empty `since` → treat as
     `DateTime.MinValue` (full pull); unparseable → 400. A `MinValue` cursor is the **normal** first request
     from a freshly restarted client (G2), not an error.
   - Project each row into a `SyncChangeDto`: `Operation = "delete"` when `IsDeleted` else `"update"`,
     `EntityId = SyncId`, `PayloadJson` serialized from typed columns via the Contracts payload DTO, carrying
     `Version` / `OriginDeviceId` / `CreatedAt`.
   - Return `SyncPullResponse { Changes, ServerTime = UtcNow }`. **The client is expected to store `ServerTime`
     as its next `since`** — that is what keeps the cursor on server time and immune to client clock skew.
     (It does not do this yet — see G2.)
   - **Boundary handling:** `UpdatedAt > since` (strict), with `ServerTime` handed back each pull; the client's
     version-based LWW dedupes anything landing exactly on the boundary. Document this.
   - **Cursor limitation (known, documented):** the `since` timestamp honors the client's existing
     `PullAsync(DateTime since)` signature. A server-assigned monotonic sequence (`bigserial server_seq`) plus
     an opaque cursor token would be strictly more robust. **Leave the seam; do not build it** — it would
     ripple into the client's `FileSyncProvider` too.

4. **Payload (de)serialization and validation** (WL-83).
   - All payload JSON goes through the Contracts payload DTOs and `SyncJson.Options` (rule G) — no hand-rolled
     JSON shapes.
   - Per-item failures return `Accepted = false` with a `Reason` and **do not 500 the batch**: unknown
     `EntityType`, unknown `Operation`, `EntityId == Guid.Empty`, `Version < 1` (client entities are born at
     version 1, so a 0 means the pusher failed to lift `Version` out of `PayloadJson` — better to say so than
     to let a version-0 row lose every comparison it will ever be in), malformed `PayloadJson`, missing payload
     on a create/update, and — see G1 — a task or time entry whose parent SyncId is `Guid.Empty`. Reject rather
     than storing an orphan; a silent `Guid.Empty` parent is invisible until S4 reports come out wrong.
   - 400 for a structurally invalid request body, 401 for missing/expired token (S2), never a raw stack trace.

## Cross-repo gaps found while reconciling (server does not fix these)

These are real client/server contract gaps found by reading client `main` @ `7ae7565`. **No client changes in
this plan** — each is filed as its own client-repo ticket (below), linked to WL-76. The server-side
accommodations listed above are what make S3 correct and safe in the meantime.

- **G1 ([WL-91](https://mnisan.atlassian.net/browse/WL-91)) — parent links don't exist on the client (blocking for real end-to-end sync).** `TaskItemPayload`
  requires `ProjectSyncId` and `TimeEntryPayload` requires `TaskSyncId`, but the client's `TaskItem` carries
  only `ProjectId` (local int) and `TimeEntry` only `TaskItemId` (local int) — `Models/TaskItem.cs:12`,
  `Models/TimeEntry.cs:10`. Neither direction works today: a pushed payload deserializes to
  `ProjectSyncId = Guid.Empty`, and a pulled payload leaves the client's local FK int at `0`. This is the same
  class of bug as WL-47/WL-53 (local int vs `SyncId`), one level down. Client fix: add
  `ProjectSyncId`/`TaskSyncId` to the client models with a backfill, populate them on write, and resolve the
  local FK from the SyncId on merge. **Server accommodation now:** reject `Guid.Empty` parents per-item with a
  clear reason (Scope 4).
- **G2 ([WL-92](https://mnisan.atlassian.net/browse/WL-92)) — the client's pull cursor is its own clock and is not persisted.** `_lastSyncedAt` starts at
  `DateTime.MinValue` and is set to the client's local `UtcNow`, never to the server's `ServerTime`
  (`Services/SyncOrchestrator.cs:38,78,83`). Consequences the server must tolerate: a full re-pull on every
  client restart, and a `since` that can be ahead of or behind server time. Client fix: persist `ServerTime`
  and feed it back (needs a `SyncOrchestrator` change, since `ISyncProvider.PullAsync(DateTime since)` has no
  channel for the server's cursor today).
- **G3 ([WL-93](https://mnisan.atlassian.net/browse/WL-93)) — client deletes never bump the version.** See rule D. The server compensates for the API path, but
  **client-to-client sync via `FileSyncProvider` still drops every delete**, since both sides use the strict
  `>` comparison. Client fix: stamp deletes (`StampForUpdate`, or an explicit version bump) in
  `LocalDbService.Delete*Async`. That is an independently valuable bug fix, not just an API concern.
- **G4 ([WL-94](https://mnisan.atlassian.net/browse/WL-94)) — push acks are keyed differently on each side.** `SyncResult.AcceptedSyncQueueIds` is a list of local
  `SyncQueue.Id` ints; the server never sees those ids and answers with `SyncItemResult.EntityId`. Rule F's
  positional contract is how `ApiSyncProvider` will bridge it. A future Contracts upgrade could add a
  client-supplied correlation id to `SyncChangeDto`; not now.

### Client plan state (context, not work for this plan)

The client repo still has unfinished plans of its own, and S3's endpoints can't be exercised end-to-end until
some of them land. As of client `main` @ `7ae7565`:

- **Plan 6 (`06-api-sync-provider.md`) — not started, but rewritten (2026-08-11) to match this contract.**
  There is no `Sync/ApiSyncProvider.cs`, and `AppSettings` has `SyncFolderPath` but **no `ApiEndpoint` /
  `ApiToken`** (`Services/SettingsService.cs:20`). It used to target the README's FastAPI service with
  client-owned stub DTOs; it now consumes `WorkLedger.Contracts`, bootstraps against
  `POST /auth/register-device` (S2) instead of a hand-entered static token, and **restates the client-facing
  half of the reconciliation rules below**. WL-91/93 block it; WL-94 is scoped inside it. **If a rule in this
  plan changes, change it there in the same breath** — the two documents are deliberately redundant so a
  one-sided edit is visible in review.
- **`SyncOrchestrator` is never constructed by the running app.** It appears only in tests and its own file —
  not in `App.axaml.cs`, not in `MainWindowViewModel`. So `FileSyncProvider` (Plan 5, merged) has no runtime
  entry point: nothing calls `RunSyncAsync` or `ConfigureSyncInterval` outside the test suite. That wiring
  belongs to **Plan 7 (`07-sync-reports-ui-and-release.md`)**, which is still open and has also been updated
  (2026-08-11) — its `Api`-mode settings now describe a device-bootstrapped JWT rather than a typed-in token,
  and its `LastSyncedAt` is called out as the same value as the pull cursor (WL-92).
- Plans 1–5 have merged (Plan 2 = WL-73; the rest were merged with placeholder ticket numbers — the client
  `plans/README.md` ticket column is still mostly `TBD`).

None of this blocks building S3 — the server is validated against its own Testcontainers tests and the
Contracts types — but "S3 is done" will not mean "sync works end to end" until Plan 6 lands with WL-91/93.

## Out of scope

- **No client changes.** G1–G4 are client-repo tickets (WL-91/92/93/94); client Plan 6 (`ApiSyncProvider`) is still unbuilt —
  there is no `Sync/ApiSyncProvider.cs` on client `main`. This plan is validated entirely against server-side
  tests + the Contracts types.
- No retry/backoff/queue-draining on the server — the client owns the queue; the server is request/response.
- No reports — S4 (WL-77).
- No monotonic-cursor implementation — leave the seam (Scope 3).
- No conflict *merge* beyond last-write-wins (no field-level merge) — matches the client's model.
- No compaction of a batch's duplicate changes for one entity — apply them in order (rule F); collapsing them
  would break the positional ack contract.

## Tests (WL-84 — Testcontainers, real Postgres, two simulated devices under one account)

- Push of a new create inserts a row scoped to the account with the right `SyncId` / `Version` / `OriginDeviceId`.
- Push with a higher `Version` **overwrites**; with a lower or equal version **the server wins**, the row is
  untouched, and the response carries the authoritative version + payload (rules A/B).
- **`UpdatedAt` is not restamped when the server wins or on a replayed no-op** — assert the column value is
  unchanged, then assert the other device's next pull does *not* re-deliver the row (rule E). This is the test
  that would catch an accidental EF write.
- A `"delete"` push **at the same version as the server row** tombstones it and bumps the version (rule D);
  a pull from the *other* device returns it as `Operation = "delete"` with the bumped version. Also cover a
  delete for a `SyncId` the server has never seen (tombstone inserted).
- A replayed delete against an already-tombstoned row does not bump the version again.
- Pull returns only `UpdatedAt > since` changes from **other** devices (the caller's own writes excluded),
  scoped to the caller's account — a second account's data never appears (the multi-tenant isolation test).
- **Two devices, colliding local Ids, distinct SyncIds** both survive a full push/pull round-trip as separate
  entities — the end-to-end mirror of the client's `RunSyncAsync_TwoDevicesWithCollidingLocalIds` regression.
- Re-pushing an already-applied change is an idempotent no-op (version not bumped).
- `ServerTime` from a pull, fed back as the next `since`, does not re-return the same changes; `since` omitted
  or `DateTime.MinValue` returns everything (G2).
- A batch mixing entity types applies parents before children, and per-entity ordering within the batch is
  preserved (create → update → delete for one entity ends tombstoned).
- Per-item validation: unknown `EntityType`, malformed `PayloadJson`, and a `Guid.Empty` parent SyncId each
  fail **only that item** while the rest of the batch commits.
- Payload round-trip: a pushed payload with `Unspecified`-kind timestamps and PascalCase property names is
  stored correctly, and the payload returned on pull is **PascalCase** (rules G/H) — assert on the raw JSON
  string, not just on a deserialized object, or the casing bug passes silently.
- `POST /sync/push` and `GET /sync/pull` return 401 without a token.

## Acceptance criteria

- `POST /sync/push` and `GET /sync/pull` exist, require auth, and are scoped to the token's account.
- Last-write-wins matches the client's rule exactly — strictly higher version wins, equal/lower loses, **no
  timestamp tie-break** — and rules A–H are documented in code with their reasons.
- Deletes tombstone with a server-side version bump and propagate to the other device's pull.
- Matching is by `(AccountId, SyncId)`; the caller's own writes are excluded from its pulls.
- Cross-account isolation is enforced and tested.
- The colliding-local-Id / distinct-SyncId round-trip passes end-to-end through the API.
- Pushes are transactional, positionally acked, and idempotent on replay; a losing or no-op item performs no
  write and therefore does not disturb any other device's cursor.
- Payload JSON is PascalCase out / case-insensitive in, and all timestamps are normalized to UTC.
- All tests pass under Testcontainers in CI.

## Suggested commit message

```
feat(sync): implement /sync/push and /sync/pull with last-write-wins reconciliation
```

## Prompt for Claude Code

> Implement `POST /sync/push` and `GET /sync/pull?since=<ISO8601>` in `WorkLedger.Server` (WL-76), both
> authorized (the app already has an auth fallback policy — do not add `.AllowAnonymous()`) and scoped to the
> token's `AccountId` via `ICurrentContext`. Use the existing Contracts DTOs; add `SyncEntityTypes` /
> `SyncOperations` constants (`"project"`/`"task"`/`"timeentry"`, `"create"`/`"update"`/`"delete"`) and a
> shared `SyncJson.Options` (default PascalCase naming, `PropertyNameCaseInsensitive = true`) to Contracts.
> **Read the "Reconciliation rules" section of `plans/S3-sync-endpoints.md` and implement rules A–H exactly,
> comments and all** — in particular: LWW is a bare `incoming.Version > existing.Version` with **no**
> `CreatedAt` tie-break; deletes are accepted at `>=` and stored as tombstones with `Version + 1`; a losing or
> replayed item must perform **no** EF write at all (an accidental write restamps `UpdatedAt` and re-delivers
> the row to every other device); results are returned one-per-change in request order; payload JSON is
> PascalCase out and case-insensitive in; every incoming `DateTime` is normalized to UTC before it reaches
> Npgsql. Stable-sort each batch projects → tasks → time entries, preserving order within a type, and wrap it
> in a transaction. For pull, return this account's rows where `UpdatedAt > since AND OriginDeviceId <>
> callerDevice` (tombstones included, as `"delete"` changes) plus `ServerTime = UtcNow`; treat a missing or
> `DateTime.MinValue` `since` as a full pull. Fail individual items (unknown entity type/operation, malformed
> payload, `Guid.Empty` parent SyncId) with a reason instead of 500-ing the batch. Write Testcontainers tests
> covering the list in the Tests section. Do not modify the client repo — gaps G1–G4 in the plan are separate
> client-side tickets (WL-91/92/93/94).
