# Plan S2 — Auth (JWT bearer, device-scoped) (ticket TBD)

## Context

Client Plan 6 sends a **bearer token** stored in `AppSettings.ApiToken` and hits `POST /sync/push` /
`GET /sync/pull` with `Authorization: Bearer {token}`. It explicitly defers real auth ("a single static bearer
token stored in settings is sufficient for this stub; real auth is explicitly deferred until the backend
exists"). The backend now exists — so this plan builds **real JWT bearer auth**, but scoped to what v1 actually
needs: a **single self-hosted account** with **per-device tokens**. Multi-user accounts, refresh tokens, and a
full registration UX are deferred (they're a later additive change, since S1 already has `Account`/`Device`).

Every sync request must resolve to `(accountId, deviceId)` from the token so S3 can scope all data access to
that account and stamp/compare origin correctly. Auth is the gate that makes multi-tenancy safe: without it,
`/sync/pull` would leak every account's data.

## Scope

1. **JWT bearer validation.** Add `Microsoft.AspNetCore.Authentication.JwtBearer`. Configure JWT bearer auth
   with signing key, issuer, and audience read from config/env (`JWT__Signing Key`, etc. — **never hardcode**;
   fail fast on startup if the signing key is missing). Add `app.UseAuthentication()/UseAuthorization()` and an
   `.RequireAuthorization()` default so every endpoint is protected unless explicitly `.AllowAnonymous()`
   (`/health`, `/`, and the bootstrap endpoint below).

2. **Claims.** Tokens carry at minimum: `sub` = device DB id (or DeviceId string), a custom `account_id` claim,
   and the `DeviceId` string (so S3 can set/compare `OriginDeviceId` without a DB lookup). Add a small
   `ICurrentDevice`/`CurrentContext` accessor that reads these off `HttpContext.User` so endpoints get
   `(AccountId, DeviceId)` cleanly.

3. **v1 account seeding.** On startup (or via a one-time migration/seed), ensure the single v1 `Account` exists
   (id/name from config, e.g. `ACCOUNT__NAME`). This is the account every device attaches to in v1.

4. **Device bootstrap → token issuance.** A device needs a way to get its first token. Implement a minimal,
   **config-gated** bootstrap:
   - `POST /auth/register-device` (AllowAnonymous, but gated by a **bootstrap secret** from config —
     `BOOTSTRAP__SECRET` sent as a header or body field): body includes the client's `DeviceId` (its per-install
     GUID string) and an optional label. If the bootstrap secret matches, upsert a `Device` row under the v1
     account and return a signed JWT (long-lived is acceptable for v1 self-hosted; document the expiry choice).
   - If the `DeviceId` already has a `Device` row, re-issue a token (idempotent) rather than erroring — devices
     re-registering after losing their token is a normal case.
   - This keeps token issuance real (signed JWTs, device identity) without building user accounts/passwords.
     Note clearly in comments that multi-user auth (per-user accounts, login, refresh) is the deferred
     follow-up.

5. **401/403 behavior.** Unauthenticated → 401; authenticated but wrong/missing account scoping → 403. Make
   sure a malformed or expired token fails cleanly (401, no stack trace leak).

6. **Tests** (Testcontainers + `WebApplicationFactory`):
   - `/health` is reachable without a token; `/sync/*` (once they exist) and a test protected endpoint return
     401 without a token.
   - `POST /auth/register-device` with the correct bootstrap secret returns a valid JWT; with a wrong/missing
     secret returns 401/403.
   - A token issued by bootstrap is accepted by a protected endpoint and resolves to the right
     `(accountId, deviceId)` (assert via a tiny authenticated `GET /auth/whoami` test endpoint, or an existing
     protected route).
   - Re-registering the same `DeviceId` is idempotent (no duplicate `Device` rows; a fresh valid token).

## Out of scope

- No multi-user accounts, passwords, login UI, OAuth, or refresh-token rotation — single v1 account, deferred
  per client Plan 6 and `CLAUDE.md` ("`Auth/` folder is an empty placeholder").
- No client-side changes — the client's `ApiToken`/`ApiEndpoint` settings already exist (client Plan 6); this
  plan just makes the token it sends mean something. Wiring the client to actually call `/auth/register-device`
  is client-side future work.
- No rate limiting / brute-force protection on the bootstrap endpoint beyond the shared secret — note as a
  hardening follow-up for S5 or later.
- No sync logic — S3.

## Acceptance criteria

- All endpoints except `/health`, `/`, and `/auth/register-device` require a valid JWT (401 otherwise).
- `POST /auth/register-device` issues a signed JWT for a device under the v1 account only when the bootstrap
  secret matches; re-registration is idempotent.
- An issued token resolves to `(AccountId, DeviceId)` retrievable by endpoints via a current-context accessor.
- JWT signing key and bootstrap secret are config/env-driven; the app fails fast at startup if the signing key
  is absent.
- Auth tests pass under Testcontainers in CI.

## Suggested commit message

```
feat(auth): add JWT bearer auth with device bootstrap and single-account scoping
```

## Prompt for Claude Code

> Add JWT bearer authentication to `WorkLedger.Server`. Read the signing key, issuer, audience, bootstrap
> secret, and v1 account name from config/env (fail fast if the signing key is missing). Protect all endpoints
> by default except `/health`, `/`, and `POST /auth/register-device`. Seed a single v1 `Account` on startup.
> Implement `POST /auth/register-device` (AllowAnonymous, gated by a bootstrap secret header/field): given the
> client's `DeviceId` string, upsert a `Device` under the v1 account and return a signed JWT carrying
> `account_id`, the device id, and the `DeviceId` string as claims; make re-registration idempotent. Add a
> `CurrentContext` accessor exposing `(AccountId, DeviceId)` from the token, and a small authenticated
> `GET /auth/whoami` for testing. Write Testcontainers + WebApplicationFactory tests covering: 401 without a
> token, successful/failed bootstrap, token acceptance resolving to the right account/device, and idempotent
> re-registration. Do not build user accounts/passwords — note that as the deferred follow-up.
