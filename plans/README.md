# WorkLedger Server — Build Plans Index

The client-side MVP plans (`plans/01`–`plans/07`) build sync up to the point where a real backend is
required. Client Plan 6 (`06-api-sync-provider.md`) implements `ApiSyncProvider` as a **stub** against a
documented-but-nonexistent REST contract. **These server plans build the thing that contract points at.**

Stack decision (2026-08-10): **C# / ASP.NET Core Minimal APIs on .NET 10, EF Core + Npgsql → PostgreSQL,
JWT bearer auth, xUnit + Testcontainers, Docker Compose.** Chosen over the original README's FastAPI/SQLAlchemy
so the client (C#) and server share one language, one toolchain, and — critically — one **compiler-enforced
wire contract** via a shared `WorkLedger.Contracts` library, eliminating the class of client/server drift bug
that the client plans' addenda already had to patch once (`EntityId` int-vs-`Guid`).

This is a **new repository** (`WorkLedger-Server`), independent of `WorkLedger-Client`. The two repos meet
only at `WorkLedger.Contracts` (see S0).

| # | File | Ticket(s) | Depends on |
|---|------|-----------|------------|
| S0 | [S0-scaffold-and-contracts.md](S0-scaffold-and-contracts.md) | TBD | — |
| S1 | [S1-persistence-and-datamodel.md](S1-persistence-and-datamodel.md) | TBD | S0 |
| S2 | [S2-auth.md](S2-auth.md) | WL-75 (done) | S1 |
| S3 | [S3-sync-endpoints.md](S3-sync-endpoints.md) | WL-76 (WL-82/83/84) | S1, S2 |
| S4 | [S4-reports.md](S4-reports.md) | WL-77 | S1 (parallel with S3) |
| S5 | [S5-deployment-and-docs.md](S5-deployment-and-docs.md) | WL-78 | S3, S4 |

> **Ticket numbers:** like the client plans, these use `Sn` as plan IDs with tickets left **TBD** — create each
> fresh in Jira (project `WL`, cloud ID `ea0603f1-23bf-48c8-b2f9-f77728f8eb23`) when you pick it up, and it'll
> get the next available `WL-##`. Consider a Jira component or label (`server` / `backend`) to separate these
> from client tickets.

## Sequencing

- **S0 → S1 → S2** run first, in order. S1 (data model) and S2 (auth) are both prerequisites for the sync
  endpoints.
- **S3 (sync) and S4 (reports) can run in parallel** once S1 lands — S3 needs S2 (auth) as well; S4 only needs
  the data model. If reporting matters more for a near-term demo, S4 can be pulled forward.
- **S5** (deployment + docs) depends on S3 and S4 being mergeable; the release tag depends on everything.

## Cross-repo contract handoff (important)

`WorkLedger.Contracts` is authored in **this** repo (S0). The **client** consumes it only when its real
`ApiSyncProvider` is implemented (client Plan 6) — which is not now. Until then the client keeps its own stub
DTOs. When you wire the client up for real, switch it to the published `WorkLedger.Contracts` package (S0
covers the optional GitHub Packages publish step) so both sides share identical types. Do **not** modify the
client repo as part of these server plans.

## Contract inputs Claude Code will need

The server's typed columns and payload DTOs must exactly mirror the client's entity shapes. **S3 has already
done this reconciliation** — see its "Client vs server: who does what" table, the normative reconciliation
rules, and the four cross-repo gaps (G1–G4) it found, all verified against client `main` @ `7ae7565`
(2026-08-11). Start there rather than re-deriving it. For any other plan, have the exact field lists from the
client repo on hand (paste them into the session):
- `Models/Project.cs`, `Models/TaskItem.cs`, `Models/TimeEntry.cs` (including the `Version` / `OriginDeviceId`
  / `SyncId` fields added in client Plans 2 and its addendum)
- `Models/SyncQueue.cs` and `Sync/RemoteChange.cs` (the wire shape the client already emits/expects)

## House style (same as client plans)

Each plan is self-contained and pasteable as the first message of a fresh Claude Code session: **Context /
Scope / Out of scope / Acceptance criteria / Suggested commit message / Prompt for Claude Code.** Conventional
Commits drive versioning (`feat:` minor, `fix:` patch, `chore:`/`docs:`/`refactor:` no bump). Work each on its
own branch off `main`, one PR each.
