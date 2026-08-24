# Plan S7 — Login, device enrollment under a user, and token revocation (ticket TBD)

## Context

S6 gave a `User` an email and a password hash but no way to use them: the only token source is still
`POST /auth/register-device`, gated by a shared secret and hard-wired to the single user. S7 replaces it.

The shape to keep in mind: **credentials are user-level, tokens are device-level.** A person logs in *from a
device*, and what comes back is a JWT scoped to `(user, device)` — because everything downstream needs both.
`SyncService` compares `OriginDeviceId` to decide what to echo back on a pull, and the whole tenancy invariant
is that `UserId` comes from the token. So login takes the device identity in the request and mints a token
carrying both, which is precisely what `/auth/register-device` did — minus the shared secret, plus a password.

The token itself stays **long-lived** (`Jwt:ExpiryDays`, default 365). Short access tokens plus rotating
refresh tokens were considered and deferred: the client is an offline-first desktop app that may go weeks
between syncs, a 15-minute access token buys nothing when the refresh token sitting next to it in
`AppSettings` is the real credential, and rotation is a meaningful chunk of work. What the long token *does*
need is a way to be killed, which is scope item 3 — **the security stamp**. That is the substitute for
short expiry, and it is what makes S8's account deletion and device revocation actually mean something.

## Scope

1. **`POST /auth/login`** (AllowAnonymous). Body
   `LoginRequest(string Email, string Password, string DeviceId, string? DeviceLabel)` — `DeviceId` is the
   client's per-install GUID string, same value it already sends as `OriginDeviceId`. Flow:
   - Normalize the email, look up by `NormalizedEmail`.
   - Reject if the user is missing **or** `PasswordHash` is null (the unclaimed legacy row from S6 — it must
     not be loggable-into).
   - Lockout check (item 4) before verifying, so a locked account doesn't burn hash cycles.
   - `PasswordService.Verify`; on `SuccessRehashNeeded`, rehash and store — that is the whole point of using a
     versioned hasher, and it costs one write on a login that was already writing.
   - On success: reset `FailedLoginCount`/`LockoutEndUtc`, stamp `LastLoginUtc`, **upsert the `Device` row**
     under this user (create if new, update `Label` when supplied, always bump `LastSeenUtc`) — carry the
     idempotent behavior over from `/auth/register-device` verbatim, including the unique
     `(UserId, DeviceId)` index doing the work.
   - Return `200` `LoginResponse(string Token, DateTime ExpiresAtUtc, UserSummary User)`.
   - **Failure is uniform: `401` `{ error = "Invalid email or password." }`** for unknown email, wrong
     password, and credential-less user alike. Unlike registration (which honestly 409s on a duplicate), login
     must not let an anonymous caller enumerate accounts. Note the asymmetry in a comment — it looks
     inconsistent until you know why.
   - The same `DeviceId` logging in from a second user is a **different device row** under a different user;
     nothing is shared. Worth a test.

2. **Delete `POST /auth/register-device`** and `RegisterDeviceRequest`/`RegisterDeviceResponse` from
   `WorkLedger.Contracts`. Its job is done: enrollment is now a side effect of login. Keep `Bootstrap:Secret`
   as the **registration** gate (S6) — it is no longer a token-issuing credential, which is a strict security
   improvement worth calling out in the S9 README rewrite.

3. **Security stamp validation — the revocation mechanism.**
   - `JwtTokenService` adds a `stamp` claim carrying the user's `SecurityStamp`.
   - Hook `JwtBearerOptions.Events.OnTokenValidated`: read `sub` (the device row id) and `user_id`, then look
     up the device **joined to its user** in one query, projecting `(UserId, SecurityStamp)`. Fail the token
     (→ 401) if the device row is gone, its `UserId` disagrees with the claim, or the stamp doesn't match.
     One query covers three revocations: **account deleted**, **device removed**, **stamp bumped**.
   - This puts a DB read on every authenticated request, so cache it: `IMemoryCache`, key `device:{id}`, TTL
     `Auth:StampCacheSeconds` (default `30`, `0` disables). **That TTL is the revocation lag** — a killed
     token keeps working for up to that long. Document that number as the tradeoff it is, on the events class,
     and have S8's revocation paths evict the key directly so the common case is immediate anyway.
   - Guard the ordering: this runs *after* signature/lifetime validation, so a forged token never reaches the
     database.

4. **Brute-force defense.** S2 explicitly deferred this ("no rate limiting / brute-force protection on the
   bootstrap endpoint beyond the shared secret — note as a hardening follow-up"). A password endpoint is where
   it stops being optional.
   - **Per-user lockout**: add `FailedLoginCount int` and `LockoutEndUtc timestamptz null` to `User` (its own
     migration). `Auth:MaxFailedLogins` (default 5) consecutive failures → locked for `Auth:LockoutMinutes`
     (default 15) → `423 Locked` with a `Retry-After` header. Counters reset on success. Known tradeoff to
     document: on a single-user instance, someone who knows the email can keep the owner locked out. That is
     accepted for a self-hosted app; the alternative (no lockout) is worse.
   - **Per-IP rate limit**: `builder.Services.AddRateLimiter(...)` with a fixed window partitioned on
     `HttpContext.Connection.RemoteIpAddress`, applied to `/auth/login`, `/auth/register` and `/auth/claim`
     via `.RequireRateLimiting(...)`. `Auth:LoginRateLimitPerMinute`, default 10 → `429`. Note in a comment
     that behind a reverse proxy this partitions on the proxy's IP unless `ForwardedHeaders` is configured —
     and do **not** enable `ForwardedHeaders` here on a guess; S9's README covers the proxy setup.

5. **`POST /auth/password`** (authenticated). Body `ChangePasswordRequest(string CurrentPassword, string NewPassword)`.
   Verify the current password (401 on mismatch — a stolen token must not be enough to take over the account),
   validate the new one against the same policy as registration (400), hash, **bump `SecurityStamp`**, save.
   That kills every outstanding token *including the caller's own*, so the response returns a **freshly minted
   token for the calling device**: `200` `LoginResponse`. Without that, changing your password logs you out of
   the app you just changed it in, which reads as a bug.

6. **`/auth/whoami` gets useful.** Extend `WhoAmIResponse` to `(int UserId, string Email, string? DisplayName, string DeviceId)`
   so the client can show who it is signed in as without a second call. (S6 already renamed the field.)

7. **Tests:**
   - Login: correct credentials → 200 + a token that works on `/sync/pull`; wrong password, unknown email, and
     an unclaimed credential-less user → all identical 401 bodies; `Auth:RegistrationEnabled=false` does **not**
     affect login (a closed server still lets its existing users in — easy to get wrong).
   - Enrollment: first login creates the `Device` row; logging in again with the same `DeviceId` reuses it (no
     duplicate); a new `DeviceId` adds a second row; the same `DeviceId` under two different users yields two
     independent rows and two tokens that cannot see each other's data.
   - Revocation: a valid token stops working once the user's `SecurityStamp` is bumped directly in the
     database (with `Auth:StampCacheSeconds=0` so the test isn't racing the cache); once its device row is
     deleted; once its user row is deleted.
   - Lockout: `MaxFailedLogins` bad attempts → 423 with `Retry-After`, and the *correct* password is also
     refused while locked; a successful login before the threshold resets the counter.
   - Rate limit: exceeding `LoginRateLimitPerMinute` → 429. Configure a tiny window in the test host so this
     doesn't take a minute.
   - Password change: happy path returns a working new token; the old token stops working; wrong current
     password → 401 and the password is unchanged; a too-short new password → 400.
   - Rehash-on-verify: a user whose stored hash is in an older format still logs in and the stored hash
     changes. (If constructing a legacy hash is impractical, assert the `SuccessRehashNeeded` branch on
     `PasswordService` directly and say so in the test name.)

## Out of scope

- **Refresh tokens / rotation / short access tokens.** Deferred deliberately — see Context. Record the
  reasoning as an XML doc on `JwtTokenService` so it reads as a decision, not an oversight.
- **Account deletion, device listing/removal, logout-all** — S8. S7 only builds the mechanism (stamp
  validation) those endpoints pull on.
- **Password reset / forgotten password.** Needs SMTP. A locked-out operator's recovery path is a SQL `UPDATE`
  against the volume; S9 documents it in the README troubleshooting section.
- **MFA, sessions list, "sign in with", email verification.**
- **Client changes and doc/deployment updates** — S9.

## Acceptance criteria

- `POST /auth/login` is the only way to obtain a token; `/auth/register-device` is gone from the server and
  from `WorkLedger.Contracts`.
- A token minted by login carries `user_id`, `device_id`, `sub` and `stamp`, and is accepted by `/sync/*` and
  `/reports/*` with scope resolved from the token exactly as before.
- Bumping a user's `SecurityStamp`, deleting their device row, or deleting the user invalidates outstanding
  tokens within `Auth:StampCacheSeconds`.
- Wrong password, unknown email and credential-less user are indistinguishable from the outside.
- Repeated failures lock the account; excessive requests from one IP are rate-limited.
- `POST /auth/password` rotates the password, invalidates other devices, and returns a working token for the
  caller.
- Full suite green under Testcontainers.

## Suggested commit message

```
feat(auth)!: replace device bootstrap with email/password login and token revocation

BREAKING CHANGE: POST /auth/register-device is removed; devices enroll by calling POST /auth/login
with their DeviceId. Tokens now carry a security stamp and are rejected once it changes.
```

## Prompt for Claude Code

> Read `CLAUDE.md` and `plans/S6-user-accounts-and-registration.md` first. Implement `POST /auth/login`
> (AllowAnonymous) taking email, password, `DeviceId` and an optional label: look the user up by normalized
> email, refuse users with a null `PasswordHash`, check lockout, verify with `PasswordService` (rehashing when
> the hasher signals it), reset the failure counters, stamp `LastLoginUtc`, upsert the `Device` row under that
> user with the same idempotent semantics `/auth/register-device` had, and return a JWT plus a `UserSummary`.
> Every failure mode — unknown email, wrong password, credential-less user — must return the same 401 body; add
> a comment explaining why this differs from registration's honest 409. Delete `/auth/register-device` and its
> two DTOs from `WorkLedger.Contracts`. Add a `stamp` claim carrying the user's `SecurityStamp` and validate it
> in `JwtBearerOptions.Events.OnTokenValidated` with a single query that loads the device joined to its user
> and fails the token if the device is gone, the user id disagrees, or the stamp differs — cache it in
> `IMemoryCache` for `Auth:StampCacheSeconds` (default 30, 0 disables) and document that TTL as the revocation
> lag. Add `FailedLoginCount`/`LockoutEndUtc` to `User` in a new migration and enforce `Auth:MaxFailedLogins`
> (5) / `Auth:LockoutMinutes` (15) with a 423 and `Retry-After`; add ASP.NET Core rate limiting partitioned by
> remote IP on `/auth/login`, `/auth/register` and `/auth/claim` at `Auth:LoginRateLimitPerMinute` (10) → 429.
> Add `POST /auth/password` (authenticated) that verifies the current password, applies the registration
> password policy to the new one, bumps the security stamp, and returns a fresh token for the calling device.
> Extend `WhoAmIResponse` with the email and display name. Cover all of it with Testcontainers tests — including
> that revocation actually revokes, that lockout refuses even a correct password, and that the same `DeviceId`
> under two users stays two isolated devices. Leave account deletion and device management to S8, and leave the
> client repo, `.env.example`, `docker-compose.yml`, `README.md` and `scripts/smoke-test.ps1` to S9.
