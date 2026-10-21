# Plan S5 — Deployment + Docs + Release (ticket TBD)

## Context

S0–S4 produce a working, tested API. This plan makes it **deployable** the way the README always intended
(Docker Compose, self-hosted, Postgres alongside) and squares the documentation with the actual C# stack (the
original README describes a FastAPI/SQLAlchemy backend that no longer reflects reality). It's the equivalent of
the client plans' M7/release step.

WorkLedger's positioning is self-hosted / data-ownership / no forced cloud — so the deployment target is a
single-host `docker compose up` a solo user or small team can run on a homelab box or a small VPS, matching the
README's "Docker Compose for local and production deployments / homelab setups" language.

## Scope

1. **Production Dockerfile** for `WorkLedger.Server` — multi-stage (SDK build → runtime image), non-root user,
   `ASPNETCORE_URLS`/port exposed, `/health` as the container `HEALTHCHECK` (from S0). Publish trimmed/framework-
   dependent as appropriate; keep the image lean.

2. **`docker-compose.yml`** (production, distinct from S0's `docker-compose.dev.yml`):
   - `api` service (built from the Dockerfile) + `postgres` service with a named volume for durable data.
   - `api` `depends_on` postgres **with a healthcheck condition** so it doesn't race the DB on boot.
   - All secrets/config via environment (`ConnectionStrings__Postgres`, `JWT__SigningKey`, `BOOTSTRAP__SECRET`,
     `ACCOUNT__NAME`, etc.) sourced from a `.env` file (provide `.env.example`, never commit real secrets).
   - EF migrations applied on API startup (`Database.Migrate()` per S1) so a fresh compose-up self-provisions
     the schema. Confirm the first-boot ordering (DB healthy → API migrates → API serves) works cleanly.
   - Document (don't necessarily build) the optional NGINX/Traefik TLS-termination front per the README's
     homelab note — a commented stanza or a README section is enough.

3. **Config hardening pass:**
   - Fail fast on missing critical config (signing key, connection string, bootstrap secret) — already begun in
     S2; make sure compose surfaces clear errors, not silent 500s.
   - Ensure logs don't leak secrets or tokens.
   - CORS: the client is a desktop app (not a browser origin), so CORS is likely unnecessary — confirm and, if
     so, leave it off rather than opening it wide. Document the decision.
   - Note (as follow-ups, not necessarily built here): rate-limiting the bootstrap endpoint, and JWT expiry/
     rotation — both flagged in S2 as deferred hardening.

4. **Server README** — replace the S0 stub with real operator docs: architecture (Minimal API + EF Core +
   Postgres), the endpoint list (`/auth/register-device`, `/sync/push`, `/sync/pull`, `/reports/*`, `/health`),
   required env vars, `docker compose up` quickstart, how to register a device and get a token, backup guidance
   for the Postgres volume, and the `dotnet ef migrations` workflow for schema changes.

5. **Reconcile the client repo's README (separate, small commit in the CLIENT repo — do this deliberately, not
   as part of the server branch):** update the client `README.md`'s backend/architecture section so it stops
   describing FastAPI/SQLAlchemy/PyJWT and instead points at the C# `WorkLedger-Server` (ASP.NET Core + EF Core
   + Npgsql), and fix the `/reporsts/tasks/{id}` typo. Flag this as a distinct change the user makes in the
   client repo — **the server plans otherwise never touch the client repo.**

6. **Release.** Tag the server repo's first real release (`feat:`-driven minor bump if using the same
   Conventional Commits/Release Please convention as the client; otherwise a plain `v0.1.0`/`v1.0.0` tag —
   pick and document). Ensure CI builds the Docker image on tag (optionally push to GHCR).

7. **Smoke test the whole thing** end-to-end against the composed stack (not just Testcontainers unit/
   integration tests): `docker compose up`, register a device, push a change, pull it from a second simulated
   device, hit a report endpoint. A short script or documented manual runbook is fine.

## Out of scope

- No Kubernetes/Helm, no multi-instance/HA topology — single-host compose is the v1 target.
- No managed-cloud deployment automation (that's the README's far-future "Supabase/Azure" roadmap item).
- No CI/CD to a live environment — building/publishing the image is enough; actual deploy is the operator's
  `docker compose pull && up`.
- Multi-user auth, rate limiting, token rotation — deferred (noted in S2/here), not built.

## Acceptance criteria

- `docker compose up` on a clean host brings up Postgres + API, applies migrations, and `/health` returns 200.
- All config is env-driven via `.env` (with a committed `.env.example`); no secrets in the repo.
- A full manual/scripted smoke test passes against the composed stack: register device → push → pull from a
  second device → report.
- Server README documents endpoints, env vars, quickstart, device registration, backup, and the migrations
  workflow.
- The client repo README's backend section is corrected (as a separate client-repo change).
- A tagged release exists; CI builds the image on tag.

**As built (audit, 2026-10-21).** `Jwt__Issuer`, `Jwt__Audience` and `Database__MigrateOnStartup` were
documented in the README but never passed through `docker-compose.yml`, so setting them in `.env` did
nothing under compose; they are now passed through blank like every other optional key, and `Program.cs`
treats blank as unset for them and for `Jwt__ExpiryDays` (whose `:-365` copy of the default is gone).
`.env.*` is git-ignored, bar `.env.example`.

## Suggested commit message(s)

```
feat(deploy): production Dockerfile and docker-compose (API + Postgres)
docs: operator README for self-hosted deployment
```
(The client-README correction is a separate commit in the client repo, e.g. `docs: point backend section at C# WorkLedger-Server`.)

## Prompt for Claude Code

> Make `WorkLedger.Server` deployable. Add a multi-stage production `Dockerfile` (non-root, `/health` as
> HEALTHCHECK) and a production `docker-compose.yml` with `api` + `postgres` (named volume, `depends_on` with a
> DB healthcheck condition), all config via env from a `.env` file (provide `.env.example`; keys:
> ConnectionStrings__Postgres, JWT__SigningKey, BOOTSTRAP__SECRET, ACCOUNT__NAME), applying EF migrations on API
> startup so a fresh compose-up self-provisions the schema. Do a config-hardening pass (fail fast on missing
> critical config, no secret leakage in logs, confirm CORS is unnecessary for a desktop client and leave it
> off). Rewrite the server README with architecture, the full endpoint list, required env vars, a
> `docker compose up` quickstart, device-registration steps, Postgres backup guidance, and the migrations
> workflow. Add a smoke-test script/runbook that registers a device, pushes a change, pulls it from a second
> simulated device, and hits a report against the composed stack. Tag a first release and build the image in CI
> on tag. Do not modify the client repo (the client README correction is a separate change I'll make there).
