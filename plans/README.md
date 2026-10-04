# WorkLedger Server — Build Plans Index

The client-side MVP plans (`plans/01`–`plans/07`) build sync up to the point where a real backend is
required; client Plan 6 (`06-api-sync-provider.md`) wrote `ApiSyncProvider` against a
documented-but-then-nonexistent REST contract. **These server plans built the thing that contract points
at**, and the client now talks to it for real. The client's `plans/08`–`plans/10` are the other half of
this release's cutover (login UI, capabilities, account management) and are blocked on
[S9-client-handoff.md](S9-client-handoff.md).

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
| S3 | [S3-sync-endpoints.md](S3-sync-endpoints.md) | WL-76 (WL-82/83/84) (done) | S1, S2 |
| S4 | [S4-reports.md](S4-reports.md) | WL-77 (WL-85/86/87) (done) | S1, S2 |
| S5 | [S5-deployment-and-docs.md](S5-deployment-and-docs.md) | WL-78 (done) | S3, S4 |
| S6 | [S6-user-accounts-and-registration.md](S6-user-accounts-and-registration.md) | WL-106 (WL-110/111/112/113/114/115), WL-147 (done) | S5 |
| S7 | [S7-login-and-device-enrollment.md](S7-login-and-device-enrollment.md) | WL-107 (WL-116/117/118/119/120) (done) | S6 |
| S8 | [S8-account-deletion-and-device-management.md](S8-account-deletion-and-device-management.md) | WL-108 (WL-121/122/123/124/125) (done) | S7 |
| S9 | [S9-user-accounts-cutover.md](S9-user-accounts-cutover.md) | WL-109 (WL-126…WL-132) (done) | S6, S7, S8 |
| — | [S9-client-handoff.md](S9-client-handoff.md) | WL-131 → client epic WL-105 | S9 |
| S10 | [S10-multi-user-operations.md](S10-multi-user-operations.md) | WL-150 (WL-151…WL-155, subtasks WL-156…WL-172) (done) | S9 |
| — | [S10-client-handoff.md](S10-client-handoff.md) | WL-172 (step 0 superseded by S11's handoff) | S10 |
| S11 | [S11-web-portal.md](S11-web-portal.md) | WL-179 (WL-180…WL-185, subtasks WL-186…WL-208) (done) | S10 |
| — | [S11-client-handoff.md](S11-client-handoff.md) | WL-208 → client plan 12 (WL-173); supersedes S10's step 0 | S11 |
| S12 | [S12-email-delivery.md](S12-email-delivery.md) | WL-209 (WL-210…WL-214, subtasks WL-215…WL-230) | S11 |

**S6–S9 are one release: user accounts.** S2 built auth for a single seeded account with devices enrolled by a
shared secret, and named per-user accounts as the deferred follow-up. S6–S9 are that follow-up — a **user
account becomes the tenancy root** (`Account` is renamed to `User` and gains email/password credentials), one
user owns many devices, and each device syncs into that user's rows. It is a breaking change on the wire, so
it lands as one release with a documented upgrade path (`POST /auth/claim`, then every device logs in again).

> **Ticket numbers:** S6–S9 are filed under epic **WL-104** (*User accounts: user-owned tenancy, login, and
> device management (server)*) — one `Task` per plan, `Subtask`s per scope item, labelled `server` + `auth`.
> `Blocks` links encode the linear order (WL-106 → WL-107 → WL-108 → WL-109). **WL-147** (expose the lockout
> policy on `AuthCapabilitiesResponse`) is scope for S6 item 8 but is filed as a `Task` under WL-104 rather
> than as a subtask of WL-106 — see the note at the end of that scope item. The client-side cutover S9 hands
> off is epic **WL-105**, whose first ticket (WL-133, re-vendor `WorkLedger.Contracts`) blocks everything else
> on that side. WL-105 has since grown to WL-133–139, with WL-136 split into WL-145/WL-146 and WL-139 carrying
> WL-140–144; the client's own `plans/08`–`10` map those tickets to scope items.
> S0/S1 predate ticketing and stay **TBD**. New work goes in Jira project `WL`, cloud ID
> `ea0603f1-23bf-48c8-b2f9-f77728f8eb23`; keep using the `server` / `client` labels to separate the two sides.

## Sequencing

- **S0 → S1 → S2** run first, in order. S1 (data model) and S2 (auth) are both prerequisites for the sync
  endpoints.
- **S3 (sync) and S4 (reports) can run in parallel** once S1 and S2 land — S4 reads only the data model, but
  its endpoints are account-scoped off the caller's token, so it needs auth too (the table above says S1, S2;
  the "S4 only needs the data model" reading was wrong and is corrected here). If reporting matters more for
  a near-term demo, S4 can be pulled forward.
- **S5** (deployment + docs) depends on S3 and S4 being mergeable; the release tag depends on everything.
- **S6 → S7 → S8 are strictly linear**, and each leaves `main` shippable: S6 renames the tenancy root and adds
  registration while keeping `/auth/register-device` as the token source, S7 replaces it with login and adds
  the security-stamp revocation, S8 spends that mechanism on device management and account deletion. Don't
  parallelise them — they touch the same entity, the same claims and overlapping migrations.
- **S9 ran last** and only after all three: it audited the settled contract, rewrote the operator docs and
  deployment config, reworked the smoke test, and produced the client handoff. Re-vendoring after each of
  S6–S8 would have meant three client PRs chasing a moving contract. It deliberately added **no server
  behaviour** and **did not touch the client repo** — the re-vendor itself is client-repo work (WL-133).

**S10 is the next release, and it is not more tenancy.** S6–S9 finished multi-user, multi-device; S10 builds
the *operations* layer those plans deliberately deferred while the only user was the operator — invites
instead of a shared signup secret, an `IsAdmin` operator surface instead of `psql`, admin-issued password
resets, and the proxy-aware rate limiting Program.cs currently documents as an open problem. It adds no
tenancy concept and changes no sync or report behaviour, and every contract change is additive so the
client's vendored copy keeps compiling until it is re-taken.

> **S10 ticket numbers:** epic **WL-150**, one `Task` per scope group (WL-151 invites, WL-152 operator
> surface, WL-153 password reset, WL-154 rate limiter, WL-155 contract/tests/docs) with subtasks
> WL-156–WL-172, labelled `server` + `auth`. `Blocks` links encode the build order, which is **not** the
> lettering: WL-152 → WL-151 → WL-153 → WL-155, with WL-154 independent. The plan's Sequencing section says
> why B comes before A.

**S10's handoff is much smaller than S9's, and that is the measurable result of the additive-only rule.**
Where S9 deleted two records and reshaped a third — breaking every call site in `ApiSyncProvider` on
purpose — S10 added seven records and gave two existing ones a trailing defaulted parameter, so the
client's build stays green across the re-vendor with no source change at all. See
[S10-client-handoff.md](S10-client-handoff.md), which checks that claim call site by call site rather
than asserting it. The same document records the audit that `Sync/`, `Payloads/` and `Reports/` are
**byte-identical** to the commit the client is pinned at.

**S11 is the web portal, and it changes who owns accounts.** Decided 2026-09-25: the desktop client is
reduced to time tracking plus signing in, and **every account operation moves to the server** — registration,
claim, password change and reset, device management, sign-out-everywhere, account deletion, and the whole
operator surface — through Razor Pages served by this process under `/portal`, for regular users and admins
alike. The client stays fully usable with no server; the server, with its portal, stays fully usable with no
client. The one architectural decision is a **user-level cookie scheme beside the device-level JWT one**,
validated against the same user row and never accepted by the JSON API (see S11's Context for the options
weighed). The portal is **off by default** (`Web:Enabled`), so an upgraded deployment behaves exactly as S10
until the operator turns it on. The S6–S8 JSON account routes stay as documented **compatibility routes** for
v0.1.x clients; no new behaviour lands on them. The client half is the rewritten client plan 12
(`12-account-lifecycle-to-web-portal.md`, epic WL-173), which replaced the original S10 follow-on (invite
and reset UI in the desktop app) before it was started.

> **Build order for S11 is A → B → D → E → C → F**, not the lettering — see its Sequencing section. Group A
> is a pure refactor (no test modified) and lands alone.

**S11's handoff is the smallest yet, and it covers S10's too.** The client never re-vendored at S10's
`1e343c8`, so [S11-client-handoff.md](S11-client-handoff.md) is the one pass for both releases. Because
S11 moved the admin shapes out of the library rather than leaving them for the client to copy, the whole
difference between the client's pin and the new one is **three trailing, defaulted parameters** on two
records — S10's `RegisterUserRequest.InviteToken` and `AuthCapabilitiesResponse.RegistrationAcceptsInvite`,
and S11's `AuthCapabilitiesResponse.PortalUrl`. `Sync/`, `Payloads/` and `Reports/` are byte-identical to
the pin, audited.

## Cross-repo contract handoff (important)

`WorkLedger.Contracts` is authored in **this** repo (S0). The client **consumes it today**: client Plan 6
shipped the real `ApiSyncProvider`, and rather than take a package it keeps a **byte-identical vendored copy**
at `Sync/Contracts/`, with the source commit recorded in `Sync/Contracts/README.md` — pinned at server
`3d27c86`, re-vendored 2026-09-15 under WL-133. (`ReportDtos.cs` is deliberately not vendored; the client
computes its own reports and never calls `/reports/*`.) A cross-repo `ProjectReference` was rejected because the client's CI checks out
only its own repo, and this repo publishes no package.

The consequence to keep in mind: **changing `WorkLedger.Contracts` is a two-repo change**, and nothing detects
drift automatically — the client's `SyncContractGuardTests` only checks the vendored copy against the
*client's* own expectations, never against this repo. So a contract change means deliberately re-taking the
copy and bumping the pin. S6–S8 changed the contract three times; **S9 is the single cutover pass**, and what
it produces is [S9-client-handoff.md](S9-client-handoff.md) — the settled inventory of what was added,
removed and changed, plus the list of client files that break. The re-vendor itself is client-repo work
(WL-133), and it blocks the rest of client epic WL-105. **S10 then did the same thing again, cheaply:**
four scope groups changed the contract, and [S10-client-handoff.md](S10-client-handoff.md) is the single
pass at the end of them, and [S11-client-handoff.md](S11-client-handoff.md) the pass after that, covering
both. **From S11 the library has a scope rule: `WorkLedger.Contracts` holds what the desktop client speaks,
and nothing else.** Shapes used only by server-side surfaces — the `/admin/*` JSON API, the portal — live in
`WorkLedger.Server`. S11 applies it by moving `AdminDtos.cs` and `ResetPasswordRequest` out, which breaks no
one because the client's pin (`3d27c86`) predates both; records the client *has* vendored stay until the next
deliberate wire-breaking release, even where the client stops calling them. Publishing the package (S0 sketched the GitHub Packages step) is the follow-up
that would remove this whole class of problem. Do **not** modify the client repo as part of these server
plans — read it, coordinate, and land client edits as client-repo work.

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
