# Plan S3 — Sync Endpoints (push / pull + last-write-wins) (ticket TBD)

## Context

This is the heart of the backend and the endpoint the client's `ApiSyncProvider` (client Plan 6) is stubbed
against. It implements the two-endpoint contract already documented on the client side and in the README:
- `POST /sync/push` — client sends a batch of queued changes; server reconciles with last-write-wins and
  returns per-item outcomes + authoritative versions.
- `GET /sync/pull?since=<ISO8601>` — server returns this account's changes newer than `since` from **other**
  devices, so the caller converges.

The server is **authoritative** (README: "authoritative datastore"). It applies the same **last-write-wins**
rule the client's `SyncOrchestrator` (client Plan 4) uses, so both sides resolve conflicts identically:
**higher `Version` wins; on equal version, later `CreatedAt` wins; on a full tie, the existing/server row wins
(no needless write).** Document this rule in code — it must match the client's tie-break exactly or the two
sides can diverge.

Hard rules inherited from S1 and the client addenda:
- **Match rows by `(AccountId, SyncId)`, never internal `Id`.**
- **Deletes are tombstones** (`IsDeleted = true` + version bump), so they propagate on pull.
- **Server stamps `UpdatedAt`** (S1) — the pull cursor is server time, never client time.
- **Scope everything to the token's `AccountId`** (S2) — a device can only ever push/pull its own account's
  data. This is the multi-tenant safety boundary; a missing `WHERE AccountId = ?` here is a data-leak bug.

## Scope

1. **`POST /sync/push`** (authorized). Body: `SyncPushRequest` (Contracts, S0). For the caller's account:
   - **Order the batch by dependency** before applying: projects → tasks → time entries. A `TimeEntry` may
     arrive in the same batch as its parent `TaskItem` (or the parent may already exist server-side); applying
     children after parents avoids dangling `TaskSyncId`/`ProjectSyncId` references. (Because links are by
     SyncId, a missing parent isn't a hard FK violation, but ordering keeps data sane and reports correct.)
   - For each `SyncChangeDto`, look up the existing row by `(AccountId, EntityId/*=SyncId*/)`:
     - **No existing row:** insert (create). Deserialize `PayloadJson` into typed columns via the Contracts
       payload DTO. Set `AccountId`, `OriginDeviceId` from the change, `Version` from the change. Accept it.
     - **Existing row, incoming wins** per LWW: update typed columns + `Version` + `OriginDeviceId`; for a
       `"delete"` operation set `IsDeleted = true`. Accept it.
     - **Existing row, server wins** per LWW: do **not** overwrite. Mark the item `Accepted = false` and return
       the server's `AuthoritativeVersion` + `AuthoritativePayloadJson` so the client can adopt the winner
       immediately (this is why `SyncItemResult` carries the authoritative payload).
   - **Self-echo:** a change whose `OriginDeviceId` equals the caller's own device is normally already the
     server's state; handle idempotently (same version → no-op accept). (The client also self-filters on pull;
     belt-and-suspenders here.)
   - **Idempotent replays:** re-pushing an already-applied change (same SyncId + same Version) must not corrupt
     state or bump the version — treat as an accepted no-op. (Client retry/backoff is future work, but the
     server must tolerate a retried batch.)
   - Do the batch in a **transaction** so a mid-batch failure doesn't half-apply.
   - Return `SyncPushResponse { Results, ServerTime = UtcNow }`.

2. **`GET /sync/pull?since=<ISO8601>`** (authorized). For the caller's account:
   - Query each synced entity `WHERE AccountId = @acct AND UpdatedAt > @since AND OriginDeviceId <> @callerDevice`
     (exclude the caller's own writes — it already has them), including tombstoned (`IsDeleted`) rows so deletes
     propagate.
   - Project each result back into a `SyncChangeDto` (`Operation` = `"delete"` when `IsDeleted`, else
     `"update"`; `PayloadJson` serialized from typed columns via the Contracts payload DTO; `EntityId` =
     `SyncId`; carry `Version`/`OriginDeviceId`/`CreatedAt`).
   - Return `SyncPullResponse { Changes, ServerTime = UtcNow }`. **The client stores `ServerTime` as its next
     `since`** — that's what keeps the cursor on server time and immune to client clock skew.
   - **Boundary handling:** use `UpdatedAt > since` (strict) with `ServerTime` handed back each pull; the
     client's own version-based LWW dedupes anything that lands exactly on the boundary. Document this.
   - **Cursor limitation (known, documented):** the `since`-timestamp cursor honors the client's existing
     `PullAsync(DateTime since)` signature, but a server-assigned monotonic sequence (`bigserial server_seq`)
     would be strictly more robust (no reliance on timestamp resolution/ties). Note this as a **future contract
     upgrade**: if pursued, add a `server_seq` column + an opaque cursor token to the pull response and evolve
     `ISyncProvider.PullAsync` on the client. Do not implement it now — it would ripple into the client's
     `FileSyncProvider` too. Just leave the seam.

3. **Payload (de)serialization** uses the Contracts payload DTOs (S0) via `System.Text.Json`, so client and
   server serialize identically. No hand-rolled JSON shapes.

4. **Validation & errors.** Reject an unknown `EntityType` or malformed `PayloadJson` per-item (mark that item
   failed with a reason, don't 500 the whole batch) unless it's a transaction-integrity issue. Return 400 for a
   structurally invalid request body, 401 for missing/expired token (S2), never a raw stack trace.

5. **Tests** (Testcontainers, real Postgres; two simulated devices under one account):
   - Push of a new create inserts a row scoped to the account with the right SyncId/Version/OriginDeviceId.
   - Push where the incoming `Version` is higher **overwrites**; where lower/equal-tie **the server wins** and
     the response carries the authoritative version + payload.
   - A `"delete"` push tombstones the row (not a hard delete) and a subsequent pull from the *other* device
     returns it as an `Operation = "delete"` change.
   - Pull returns only `UpdatedAt > since` changes from **other** devices (caller's own writes excluded), scoped
     to the caller's account (a second account's data never appears — the multi-tenant isolation test).
   - **Two devices, colliding local Ids, distinct SyncIds** both survive a full push/pull round-trip as separate
     entities (the exact regression the client's `RunSyncAsync_TwoDevicesWithCollidingLocalIds` test guards —
     prove it end-to-end through the API too).
   - Re-pushing an already-applied change is an idempotent no-op (version not bumped).
   - `ServerTime` from a pull, fed back as the next `since`, does not re-return the same changes.

## Out of scope

- No retry/backoff/queue-draining on the server — the client owns the queue (client Plans 3/4); the server is
  request/response. `SyncResult` shape allows for client-side retry, but the server implements none.
- No reports — S4.
- No client changes — the client's real `ApiSyncProvider` consumption of these endpoints is client Plan 6
  future work; this plan is validated entirely against server-side tests + the Contracts types.
- No monotonic-cursor implementation — leave the seam, don't build it (see 2's cursor note).
- No conflict *merge* beyond last-write-wins (no field-level merge) — matches the client's LWW model.

## Acceptance criteria

- `POST /sync/push` and `GET /sync/pull` exist, require auth, and are scoped to the token's account.
- Last-write-wins reconciliation matches the client's rule exactly (higher version wins; later `CreatedAt`
  breaks ties; full tie → server keeps its row) and is documented in code.
- Matching is by `(AccountId, SyncId)`; deletes are tombstones and propagate on pull; the caller's own writes
  are excluded from its pulls.
- Cross-account isolation is enforced and tested (no account sees another's data).
- The colliding-local-Id / distinct-SyncId round-trip passes end-to-end through the API.
- Pushes are transactional and idempotent on replay; the `since`/`ServerTime` cursor doesn't re-deliver changes.
- All tests pass under Testcontainers in CI.

## Suggested commit message

```
feat(sync): implement /sync/push and /sync/pull with last-write-wins reconciliation
```

## Prompt for Claude Code

> Implement `POST /sync/push` and `GET /sync/pull?since=<ISO8601>` in `WorkLedger.Server`, both authorized and
> scoped to the token's `AccountId` (S2). Use the Contracts DTOs from S0. For push: order the batch
> projects→tasks→time entries, match existing rows by `(AccountId, SyncId)` (never internal Id), and apply
> last-write-wins — higher `Version` wins, later `CreatedAt` breaks ties, full tie keeps the server row;
> deletes set `IsDeleted` (tombstone) with a version bump; when the server wins, return `Accepted=false` with
> the authoritative version + payload; make replays idempotent; wrap the batch in a transaction; return
> `ServerTime=UtcNow`. For pull: return this account's rows where `UpdatedAt > since AND OriginDeviceId <>
> callerDevice` (including tombstones as `"delete"` changes), projected to `SyncChangeDto`, plus
> `ServerTime=UtcNow` for the client to use as its next `since`. Use the Contracts payload DTOs for all JSON.
> Document the LWW tie-break and the fact that the timestamp cursor is a deliberate v1 choice with a
> monotonic-`server_seq` upgrade left as a seam. Write Testcontainers tests with two devices under one account
> covering create/higher-version-overwrite/server-wins/tombstone-propagation/cross-account-isolation/colliding-
> local-Ids-distinct-SyncIds/idempotent-replay/cursor-not-re-delivering.
