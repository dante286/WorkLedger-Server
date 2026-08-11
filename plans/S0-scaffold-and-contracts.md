# Plan S0 — Solution Scaffold + Contracts Library + CI (ticket TBD)

## Context

This is a **new repository** — there is no existing server code. Everything downstream (persistence, auth,
sync, reports) needs a solution to live in, a shared contract types library, a test project wired into CI, and
a local Postgres to develop against. This plan builds only that foundation: a solution that **builds, runs, and
answers a health check** — no business logic yet.

The whole reason for choosing C# over the README's FastAPI plan is a **compiler-enforced wire contract** shared
between client and server. That contract library (`WorkLedger.Contracts`) is created here. It is authored in
this repo; the client only consumes it later (see repo README's "Cross-repo contract handoff"). Do **not** touch
the `WorkLedger-Client` repo in this plan.

## Scope

1. **Create the repository + solution.**
   - New Git repo `WorkLedger-Server`, `main` branch, standard .NET `.gitignore`.
   - `WorkLedger-Server.slnx` (SLNX to match the client repo's solution format) with three projects:
     ```xml
     <Solution>
       <Project Path="src/WorkLedger.Server/WorkLedger.Server.csproj" />
       <Project Path="src/WorkLedger.Contracts/WorkLedger.Contracts.csproj" />
       <Project Path="tests/WorkLedger.Server.Tests/WorkLedger.Server.Tests.csproj" />
     </Solution>
     ```

2. **`src/WorkLedger.Server`** — ASP.NET Core Minimal API, `net10.0` (match the client's target framework).
   - `dotnet new web` as the base. Enable nullable + implicit usings.
   - `Program.cs` builds a minimal app with:
     - A `GET /health` endpoint returning `{ "status": "ok", "utc": <server time> }` (200). This becomes the
       Docker/compose healthcheck target in S5.
     - A `GET /` root returning the app name + version (read from assembly informational version).
   - Serilog (or built-in `ILogger` — pick one, keep it simple) for request logging. No auth, no DB yet.
   - `ProjectReference` to `WorkLedger.Contracts`.

3. **`src/WorkLedger.Contracts`** — a plain class library, `net10.0`, **no ASP.NET/EF dependencies** (it must be
   safe for the client to reference later without dragging server packages in). Define the sync wire types up
   front so S3 and the future client both bind to them. Mirror the client's `RemoteChange`/`SyncQueue` shape
   exactly — see the field notes below and confirm against the client's `Sync/RemoteChange.cs`:
   ```csharp
   namespace WorkLedger.Contracts.Sync;

   // One create/update/delete event for one entity. EntityId is the entity's cross-device
   // SyncId (Guid), NOT any local autoincrement Id — this is the exact lesson from the client
   // plans' addenda; keying off a local int silently collides across devices.
   public sealed record SyncChangeDto(
       string   EntityType,     // "project" | "task" | "timeentry" (lowercase, matches client SyncQueue)
       Guid     EntityId,       // = entity SyncId
       string   Operation,      // "create" | "update" | "delete"
       string?  PayloadJson,    // serialized entity payload (see entity payload DTOs below); null allowed for deletes
       int      Version,
       string   OriginDeviceId,
       DateTime CreatedAt);     // client-side change time (informational / tie-break only, never trusted as the pull cursor)

   public sealed record SyncPushRequest(IReadOnlyList<SyncChangeDto> Changes);

   // Per-item outcome so the client can reconcile immediately rather than waiting for the next pull.
   public sealed record SyncItemResult(
       Guid    EntityId,
       bool    Accepted,               // true = server took this change; false = server's version won (LWW)
       int     AuthoritativeVersion,   // the version the server now holds for this entity
       string? AuthoritativePayloadJson, // present when Accepted == false so the client can adopt the winner
       string? Reason);

   public sealed record SyncPushResponse(
       IReadOnlyList<SyncItemResult> Results,
       DateTime ServerTime);           // client should store this as the "since" for its next pull

   public sealed record SyncPullResponse(
       IReadOnlyList<SyncChangeDto> Changes,
       DateTime ServerTime);           // authoritative server clock; client uses as next "since"
   ```
   Also define the **entity payload DTOs** both sides (de)serialize into `PayloadJson`, so serialization is
   identical on both ends. **Get the exact fields from the client's `Models/*.cs`** — do not invent them; the
   skeletons below are placeholders to be reconciled against the real client models:
   ```csharp
   namespace WorkLedger.Contracts.Payloads;

   public sealed record ProjectPayload(
       Guid SyncId, string? Name, string? ProjectCode, string? Description,
       DateTime CreatedAt, DateTime? UpdatedAt, int Version, string? OriginDeviceId,
       bool IsArchived /* confirm against client model */);

   public sealed record TaskItemPayload(
       Guid SyncId, Guid ProjectSyncId /* parent by SyncId, NOT local Id */,
       string? Name, /* ...confirm remaining fields... */
       DateTime CreatedAt, DateTime? UpdatedAt, int Version, string? OriginDeviceId, bool IsArchived);

   public sealed record TimeEntryPayload(
       Guid SyncId, Guid TaskSyncId /* parent by SyncId */,
       DateTime StartUtc, DateTime? EndUtc, /* or a Duration — confirm which the client stores */
       DateTime CreatedAt, DateTime? UpdatedAt, int Version, string? OriginDeviceId);
   ```
   > **Flag if unclear:** if the client's `TimeEntry` stores a computed duration vs. start/end, or if
   > task→project / entry→task links are stored differently, stop and reconcile before finalizing these DTOs —
   > S4 (reports) sums durations and S3 resolves parent links **by SyncId**, so getting these wrong is expensive
   > to unwind later.

4. **`tests/WorkLedger.Server.Tests`** — xUnit, `net10.0`, referencing both other projects. One real starter
   test: a `WebApplicationFactory<Program>`-based test hitting `GET /health` and asserting 200 + `status: ok`.
   (This requires `Program` to be partial/accessible — add `public partial class Program;` at the end of
   `Program.cs`, the standard Minimal-API testability shim.)

5. **Local Postgres for development** — `docker-compose.dev.yml` at repo root running **Postgres only** (no API
   container yet; that's S5), on a non-default host port to avoid clashing with any existing local Postgres,
   with a named volume and healthcheck. Document the connection string in the README. (Tests will use
   Testcontainers in S1 and won't depend on this compose file — this is purely for `dotnet run` against a real
   DB during development.)

6. **CI** — `.github/workflows/build.yml`: restore, build (`Release`), `dotnet test`. Single ubuntu-latest
   runner is fine (unlike the client's 3-OS matrix — this is a server, it only ever runs on Linux). Ensure the
   test step runs and passes the health-check test.

7. **Server README** — minimal for now: stack, how to run (`docker compose -f docker-compose.dev.yml up -d`
   then `dotnet run --project src/WorkLedger.Server`), how to test. S5 expands it.

8. **(Optional, deferrable) Contracts publishing** — set up publishing `WorkLedger.Contracts` to **GitHub
   Packages** (NuGet) on version tags, so the client can `PackageReference` it later. If this adds friction now,
   note it as a follow-up and skip — the client won't consume it until its Plan 6 anyway. **Git submodule** of
   the Contracts project into both repos is an acceptable lower-ceremony alternative; call out which you chose.

## Out of scope

- No EF Core, no database access, no real Postgres schema — S1.
- No auth/JWT — S2. `/health` and `/` are anonymous.
- No sync or reports endpoints — S3/S4.
- Do **not** modify the `WorkLedger-Client` repo. The Contracts library is authored here; the client wires it
  up in its own future work.
- No production Dockerfile / compose for the API — S5. The dev compose is Postgres-only.

## Acceptance criteria

- `dotnet build` succeeds on all three projects; `dotnet run --project src/WorkLedger.Server` starts and
  `GET /health` returns 200 with `status: ok`.
- `dotnet test` discovers and passes at least the health-check integration test.
- `WorkLedger.Contracts` compiles with **no** ASP.NET Core or EF Core package references (verify the `.csproj`
  has none — it must stay client-safe).
- `docker compose -f docker-compose.dev.yml up -d` brings up a healthy Postgres reachable at the documented
  connection string.
- CI's `build.yml` passes on push.

## Suggested commit message(s)

```
chore: scaffold WorkLedger-Server solution (Minimal API, Contracts, Tests)
feat(contracts): define sync wire DTOs and entity payload shapes
ci: build and test on push
```

## Prompt for Claude Code

> Scaffold a new .NET 10 ASP.NET Core Minimal API repo `WorkLedger-Server` with a `WorkLedger-Server.slnx`
> containing three projects: `src/WorkLedger.Server` (Minimal API, `GET /health` returning `{status:"ok"}` and
> a version root), `src/WorkLedger.Contracts` (a dependency-free class library defining the sync wire DTOs —
> `SyncChangeDto`, `SyncPushRequest/Response`, `SyncItemResult`, `SyncPullResponse` — and `ProjectPayload`/
> `TaskItemPayload`/`TimeEntryPayload`, with `EntityId`/`SyncId` as `Guid` and parent links by SyncId, mirroring
> the client's `Sync/RemoteChange.cs` and `Models/*.cs` which I'll paste), and `tests/WorkLedger.Server.Tests`
> (xUnit with a `WebApplicationFactory<Program>` test hitting `/health`). Add a `docker-compose.dev.yml` running
> Postgres only on a non-default port with a healthcheck, a `.github/workflows/build.yml` doing restore/build/
> test on ubuntu-latest, and a short README. Keep `WorkLedger.Contracts` free of any ASP.NET/EF references so it
> can be shared with the client later. Do not touch the client repo.
