# Plan S4 — Reports Endpoints (ticket TBD)

## Context

The README promises server-side reporting endpoints (`/reports/weekly`, `/reports/projects/{id}`,
`/reports/tasks/{id}`) producing "Total time per project / per task / Daily breakdowns" with optional JSON/CSV
export. The client has its own local Reports view (client Plan 7 + WL-50 CSV export), but the server owns the
**authoritative, cross-device** rollup — once multiple devices sync into one account, only the server has the
merged picture. This plan builds those endpoints against the S1 data model.

This can run **in parallel with S3** — it only reads the S1 tables (it doesn't need push/pull to exist), though
it's only *useful* once data is syncing in. It does need S2 (auth) so reports are scoped to the caller's
account.

Note the README's endpoint list has a typo (`/reporsts/tasks/{id}`) — use the corrected `/reports/tasks/{id}`.

## Scope

1. **Common rules for all report endpoints:**
   - Authorized; scoped to the token's `AccountId` (S2).
   - **Exclude tombstoned rows** (`IsDeleted = true`) from totals.
   - Time math: sum `TimeEntry` durations. If the client stores start/end, duration = `End - Start` (ignore
     open/running entries with no end, or surface them separately — confirm against the client's `TimeEntry`
     model and decide; document the choice). All times UTC; the caller can localize.
   - Resolve `TimeEntry → Task → Project` **by SyncId** (S1), not internal Ids.

2. **`GET /reports/weekly?weekOf=<yyyy-MM-dd>`** (defaults to the current week if omitted):
   - Returns totals for the ISO week containing `weekOf`: total time for the account, broken down **per
     project** and **per task**, plus a **daily breakdown** (per-day totals across the week), matching the
     README's stated report contents.
   - Define a `WeeklyReportDto` in `WorkLedger.Contracts` (reports namespace) so the shape is shareable if the
     client ever consumes the server report directly. Keep it JSON-first.

3. **`GET /reports/projects/{projectSyncId}?from=&to=`**:
   - Total time for one project over an optional date range (default: all-time or a sensible window — pick and
     document), broken down per task within that project. `projectSyncId` is the **SyncId**, not an int.

4. **`GET /reports/tasks/{taskSyncId}?from=&to=`**:
   - Total time for one task over an optional date range, with a daily breakdown. `taskSyncId` is the SyncId.

5. **CSV export.** Support `?format=csv` (or an `Accept: text/csv` header) on each endpoint, returning a
   `text/csv` download alongside the default JSON. This satisfies the README's "Optional export endpoints
   (JSON/CSV)" and lines up with the client's WL-50 CSV-export work — keep the CSV column layout sane and
   documented (e.g. weekly: `date,project,task,seconds`). No third-party CSV lib needed for something this
   simple, but one is fine if it's already pulled in.

6. **Efficiency.** Do the aggregation in the database (EF `GroupBy` translated to SQL, or raw SQL if EF's
   grouping translation is awkward for the daily breakdown) rather than pulling all rows into memory. For a
   solo/small-team dataset this isn't critical, but avoid the obvious `ToList()`-then-sum-in-C# footgun on the
   time-entry table.

7. **Tests** (Testcontainers): seed an account with a couple of projects, tasks, and time entries spanning a
   known week, then assert:
   - Weekly totals (account, per-project, per-task, per-day) match hand-computed expected values.
   - Tombstoned entries are excluded.
   - Date-range filters on the project/task endpoints clip correctly at the boundaries.
   - CSV output has the documented columns and correct rows for the same seed.
   - A second account's data never appears in the first account's reports (isolation).

## Out of scope

- No report UI — the client renders its own (client Plan 7). These are API endpoints only.
- No caching/materialized rollups — compute on request; a reporting cache is a future optimization.
- No PDF/xlsx export — JSON + CSV only, per the README.
- No analytics beyond time totals (focus time, context switching, tags are README "roadmap" items, not this).

## Acceptance criteria

- `GET /reports/weekly`, `/reports/projects/{projectSyncId}`, `/reports/tasks/{taskSyncId}` exist, are
  authorized, and are account-scoped.
- Totals are correct against seeded data, exclude tombstones, and resolve relationships by SyncId.
- `?format=csv` returns a valid `text/csv` download with documented columns on each endpoint; default is JSON.
- Date-range filters clip correctly; cross-account isolation holds.
- Aggregation happens in-database, not in memory.
- All report tests pass under Testcontainers in CI.

## Suggested commit message

```
feat(reports): add weekly/project/task report endpoints with JSON and CSV export
```

## Prompt for Claude Code

> Implement authorized, account-scoped report endpoints in `WorkLedger.Server` reading the S1 tables:
> `GET /reports/weekly?weekOf=` (account total, per-project, per-task, and daily breakdown for the ISO week),
> `GET /reports/projects/{projectSyncId}?from=&to=` (project total + per-task breakdown), and
> `GET /reports/tasks/{taskSyncId}?from=&to=` (task total + daily breakdown). IDs in routes are SyncIds (Guid),
> not internal Ids; resolve TimeEntry→Task→Project by SyncId; exclude tombstoned rows; do the aggregation in
> SQL via EF GroupBy (or raw SQL for the daily breakdown), not in memory. Add `WeeklyReportDto` etc. to
> `WorkLedger.Contracts`. Support `?format=csv` on each endpoint returning `text/csv` with documented columns
> (e.g. weekly `date,project,task,seconds`), defaulting to JSON. Confirm the TimeEntry duration/start-end shape
> against the client model I'll paste, and decide how to treat running/open entries (document it). Write
> Testcontainers tests seeding a known week and asserting weekly/project/task totals, tombstone exclusion,
> date-range clipping, CSV columns, and cross-account isolation.
