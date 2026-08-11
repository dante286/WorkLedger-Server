# Plan S1 — Persistence + Data Model (EF Core + Postgres) (ticket TBD)

## Context

S0 gave us a running API with no database. This plan adds the authoritative datastore the README always
described (`Postgres` as the "authoritative datastore"), using **EF Core + Npgsql** with EF migrations in place
of the README's SQLAlchemy/Alembic. It defines the server-side entity model and the ownership/versioning
columns the sync reconciliation (S3) and reports (S4) both depend on.

Two design rules carried straight over from the client plans' hard-won addenda:
- **Everything cross-device keys off `SyncId` (Guid), never a local autoincrement `Id`.** The client learned
  this the expensive way (its Plan 2/3/4 addenda). The server's rows use a database identity PK for internal
  joins, but **all sync lookups and all parent/child relationships resolve by `SyncId`.**
- **Deletes are tombstones, not row removals** — a delete has to propagate to other devices on pull, so it must
  participate in version comparison like any other change (soft-delete flag + version bump), not vanish.

Ownership: WorkLedger is positioned as self-hosted / offline-first for a solo or small team. v1 is
**single-account, multi-device**: one account owns all data; multiple devices of that account sync together.
The `Account`/`Device` tables exist now (so multi-user is a later additive change, not a schema rewrite), but
S2 only provisions a single account.

## Scope

1. **Add EF Core + Npgsql** to `WorkLedger.Server`: `Microsoft.EntityFrameworkCore`,
   `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design` (for migrations tooling).

2. **Entity model** (server-internal; not the wire DTOs — those live in Contracts). Suggested `Data/Entities/`:
   - `Account` — `Id` (int identity PK), `Name`/label, `CreatedAt`. (Single row in v1.)
   - `Device` — `Id` (int PK), `AccountId` (FK), `DeviceId` (string — the client's per-install GUID string,
     unique per account), `Label`, `CreatedAt`, `LastSeenUtc`.
   - `Project`, `TaskItem`, `TimeEntry` — each with:
     - `Id` (int identity PK, **internal only**)
     - `AccountId` (FK — every synced row is owned by an account)
     - `SyncId` (`Guid`, **unique per account**, indexed — the cross-device identity)
     - `Version` (int)
     - `OriginDeviceId` (string — the device that last wrote it)
     - `IsDeleted` (bool — tombstone)
     - `CreatedAt` (client-reported, informational) and `UpdatedAt` (**server-stamped on every write** — this
       is the pull cursor source of truth; never trust client clocks for it)
     - typed payload columns mirroring the client model fields (name, code, description, start/end or duration,
       archived, etc. — take exact fields from the client's `Models/*.cs`)
   - **Relationships resolve by SyncId:** `TaskItem` references its parent `Project` via `ProjectSyncId`
     (`Guid`), `TimeEntry` references its `TaskItem` via `TaskSyncId` (`Guid`). Do **not** use the internal int
     PKs as FKs across synced entities — a child arriving before its parent (S3 applies batches) must still be
     storable, so model these as plain indexed `Guid` columns (optionally a real FK with deferred/soft
     enforcement, but a plain indexed Guid + application-level ordering in S3 is simpler and matches the
     client's SyncId-keyed approach).
   - Consider a shared `IVersionedSyncEntity` interface (`SyncId`, `Version`, `OriginDeviceId`, `IsDeleted`,
     `UpdatedAt`) to keep the S3 merge logic generic — this mirrors the client's `Models/IVersionedEntity.cs`.

3. **`WorkLedgerDbContext`** with `DbSet`s for all five entities. Configure in `OnModelCreating`:
   - Unique index on `(AccountId, SyncId)` for each synced entity.
   - Unique index on `(AccountId, DeviceId)` for `Device`.
   - Index on `(AccountId, UpdatedAt)` for each synced entity (the pull query filters/sorts on this).
   - `UpdatedAt` as `timestamptz`; enforce UTC. (Npgsql maps `DateTime` with `Kind=Utc` to `timestamptz`
     cleanly — set `Kind` explicitly on write.)

4. **Server-stamped `UpdatedAt`.** Override `SaveChangesAsync` (or use an interceptor) to set `UpdatedAt =
   DateTime.UtcNow` on every added/modified synced entity, so the pull cursor can never be poisoned by client
   clock skew. This is the single most important robustness decision in the persistence layer — document it.

5. **Migrations.** Create the initial EF migration (`dotnet ef migrations add InitialCreate`). Decide and
   document the apply strategy: for dev, `dotnet ef database update`; for container startup, either
   `context.Database.Migrate()` on boot (simple, fine for single-instance self-hosted) **or** a separate
   migration step (safer for multi-instance — not a concern in v1). Pick `Migrate()`-on-boot for v1 and note
   the tradeoff. Add a `Data/README.md` snippet with the `dotnet ef` commands.

6. **Connection string / config.** Read from configuration/env (`ConnectionStrings__Postgres` or a
   `POSTGRES_*` set). Wire the `DbContext` in DI. Point local dev at the S0 `docker-compose.dev.yml` Postgres.

7. **Testcontainers test harness.** Add `Testcontainers.PostgreSql` to the test project. Build a fixture that
   spins up a throwaway Postgres container, applies migrations, and hands the test a real `WorkLedgerDbContext`.
   Starter tests:
   - Migration applies cleanly against an empty container (schema smoke test).
   - Inserting a `Project` then querying by `(AccountId, SyncId)` round-trips, and `UpdatedAt` is set by the
     server (non-null, UTC) even though the test didn't set it.
   - The `(AccountId, SyncId)` unique index rejects a duplicate.
   > Testcontainers needs a working Docker daemon in CI — GitHub's ubuntu-latest has one by default, so the S0
   > CI workflow already supports this. Confirm the test job doesn't need extra Docker setup.

## Out of scope

- No sync endpoints or merge logic — S3. This plan only defines schema + context + a way to read/write it.
- No auth — S2. (The `Account`/`Device` tables exist here; **provisioning/tokens** are S2.)
- No reports queries — S4.
- No multi-user account management, no per-user isolation testing beyond the `AccountId` column existing —
  single account in v1.
- No seed data beyond what a test fixture needs (the single v1 account is seeded in S2 alongside auth).

## Acceptance criteria

- `WorkLedgerDbContext` with `Account`, `Device`, `Project`, `TaskItem`, `TimeEntry`; each synced entity has
  `SyncId`/`Version`/`OriginDeviceId`/`IsDeleted`/`CreatedAt`/`UpdatedAt`/`AccountId` and typed payload columns
  matching the client models.
- Parent links (`TaskItem.ProjectSyncId`, `TimeEntry.TaskSyncId`) are `Guid`, indexed, and keyed on SyncId —
  **not** internal int PKs.
- Initial EF migration exists and applies cleanly to an empty Postgres.
- `UpdatedAt` is server-stamped on every write (proven by a test that never sets it).
- Unique `(AccountId, SyncId)` and `(AccountId, DeviceId)` indexes exist and are enforced.
- Testcontainers-based tests pass locally and in CI.

## Suggested commit message

```
feat(data): add EF Core model, DbContext, and initial Postgres migration
```

## Prompt for Claude Code

> Add EF Core + Npgsql to `WorkLedger.Server`. Define server entities `Account`, `Device`, and the synced
> entities `Project`/`TaskItem`/`TimeEntry`, each with an internal int PK plus `AccountId`, `SyncId` (Guid,
> unique per account, indexed), `Version` (int), `OriginDeviceId` (string), `IsDeleted` (tombstone bool),
> client-reported `CreatedAt`, server-stamped `UpdatedAt` (timestamptz, indexed with AccountId), and typed
> payload columns matching the client's `Models/*.cs` which I'll paste. Model `TaskItem→Project` and
> `TimeEntry→TaskItem` links as indexed `Guid` SyncId columns, never internal int PKs. Build
> `WorkLedgerDbContext` with unique `(AccountId, SyncId)` and `(AccountId, DeviceId)` indexes, override
> `SaveChangesAsync` to stamp `UpdatedAt = UtcNow` on every write, wire the connection string from config, and
> create the initial EF migration applied via `Database.Migrate()` on boot. Add Testcontainers.PostgreSql to
> the test project with a fixture that starts Postgres, migrates, and provides a real DbContext, plus tests for
> migration apply, SyncId round-trip with server-stamped UpdatedAt, and the unique-index rejection.
