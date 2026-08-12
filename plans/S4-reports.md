# Plan S4 — Reports Endpoints — WL-77 (WL-85/86/87)

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

## Decisions settled while implementing

The scope above leaves several calls to the implementer ("pick and document"). These are the answers, and
they are enforced by tests. The full reasoning lives in code comments — `ReportService` holds the counting
rules, `ReportQuery` the SQL, `ReportCsv` the column layouts.

1. **Running entries are excluded from every total, and counted.** Confirmed against the client:
   `TimerService` writes `DurationSeconds` only when a timer *stops*, so a running entry has a null
   `EndTime` **and** a null duration — there is no length to sum. Counting `now − start` would make the
   same report return a different number every second and would disagree with the client's own weekly
   rollup, which filters on `EndTime != null` too. Every report DTO therefore carries a `RunningEntries`
   count, so a caller can say "plus one timer still going" rather than quietly under-reporting.

2. **Duration is the stored `DurationSeconds`, falling back to `EndTime − StartTime`.** The stored column
   is what the client sums, so using it makes the server agree with the client; the fallback stops a
   finished row that somehow lacks one from counting as zero.

3. **An entry lands wholly in the UTC day it started.** Matches the client (its rollup filters
   `entry.StartTime >= weekStart`). An entry spanning midnight is not split — splitting would be more
   precise but would disagree with the client for no benefit anyone asked for.

4. **Tombstones are excluded along the whole chain**, not just the entry: an entry counts only if it, its
   task, and that task's project are all alive. An entry whose parent row has not arrived yet does not
   count either — the joins are inner joins on SyncId. The alternative (an "unattributed" bucket) would
   mean the account total stopped being the sum of the per-project totals, and the condition is transient
   anyway, since push applies parents before children.

5. **Ranges are inclusive whole UTC days.** `from`/`to` are `yyyy-MM-dd`; `from=X&to=X` is all of day X.
   Either end may be omitted and the default is **all-time**. `from > to` is a 400, not an empty report —
   an empty report reads as "you logged no time then", which is the wrong thing to tell someone who typed
   the dates backwards.

6. **`weekOf` names a week, not a start date.** Any day in the ISO week resolves to that Monday–Sunday;
   omitting it means the current UTC week. `Days` is always seven entries, zero-filled, so a caller can
   chart the week without filling gaps.

7. **404, not an empty 200, for an unknown or tombstoned project/task** — including one belonging to
   another account, which is how account scope stays unobservable.

8. **CSV columns** — each file is that report's own breakdown flattened, plus the names needed to identify
   a row once the file is detached from the request:
   - `/reports/weekly` → `date,project,task,seconds` (the day × project × task grid the JSON's three
     breakdowns are each folded from, so every one of them is a single pivot away in a spreadsheet)
   - `/reports/projects/{id}` → `project,task,seconds`
   - `/reports/tasks/{id}` → `date,project,task,seconds`

   RFC 4180 quoting, CRLF line endings, no row for a day with no time, and no `RunningEntries` — a
   header-level scalar has no honest column in a table of recorded time.

9. **`?format=csv` or `Accept: text/csv`; the query parameter wins.** A wildcard `Accept: */*` — what most
   HTTP clients send by default — stays JSON. An unrecognized `format` is a 400 rather than a silent
   fallback to JSON, which a caller's CSV parser would report as a corrupt file.

10. **Aggregation is one raw-SQL `GROUP BY` at (UTC day × task) grain**, the finest any of the three
    reports needs; the other views are folded from it in memory over a result set bounded by days × tasks.
    Raw SQL rather than EF `GroupBy` for two reasons the plan anticipated: the columns are `timestamptz`,
    where LINQ's date-truncation translations run through the *session's* TimeZone (making day boundaries
    depend on server configuration — `AT TIME ZONE 'UTC'` here cannot drift), and the joins are on
    (AccountId, SyncId) pairs with no EF navigation properties, because S1 deliberately declares none.
    The `TimeEntries` table is summed in Postgres and never materialized.

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
