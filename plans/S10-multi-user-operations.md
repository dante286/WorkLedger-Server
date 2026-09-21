# Plan S10 — Multi-user operations: invites, an operator surface, and password reset

**Tickets:** [WL-150](https://mnisan.atlassian.net/browse/WL-150) (*Multi-user operations: invites, an
operator surface, and password reset (server)*) — one `Task` per scope group, `Subtask`s per item, labelled
`server` + `auth`:

| Group | Task | Subtasks |
|---|---|---|
| A. Invites | [WL-151](https://mnisan.atlassian.net/browse/WL-151) | WL-162 (entity + `SingleUseToken`), WL-163 (register gate), WL-164 (`SignupSecretEnabled` + capabilities) |
| B. Operator surface | [WL-152](https://mnisan.atlassian.net/browse/WL-152) | WL-156 (migration), WL-157 (first admin), WL-158 (principal + disabled), WL-159 (endpoints), WL-160 (self / last-admin guards), WL-161 (audit line) |
| C. Password reset | [WL-153](https://mnisan.atlassian.net/browse/WL-153) | WL-165 (entity), WL-166 (admin mints), WL-167 (user redeems) |
| D. Rate limiter | [WL-154](https://mnisan.atlassian.net/browse/WL-154) | WL-168 |
| E. Contract/tests/docs | [WL-155](https://mnisan.atlassian.net/browse/WL-155) | WL-169 (contracts), WL-170 (tests), WL-171 (docs), WL-172 (client handoff) |

`Blocks` links encode the order in Sequencing below: WL-152 → WL-151 → WL-153 → WL-155, and WL-154 → WL-155.
The invite management routes belong to A by subject but are specified in **WL-159** with the other admin
routes. The client-side work this implies is a separate epic — see `S10-client-handoff.md` (WL-172).

## Context

**Multi-user, multi-device is already done.** S6–S9 made the `User` row the tenancy root, gave it
email/password credentials, made `POST /auth/login` the only token source, and scoped every sync and report
query on the `user_id` claim. The unique index on `Devices` is `(UserId, DeviceId)`, so the same install GUID
under two users is two unrelated rows, and nothing anywhere reads a user identifier from a body or a URL.
There is no single-account assumption left in the data model, the merge loop or the report SQL. Do not
"add multi-user support" — verify the above and move on.

What is still single-operator-shaped is everything *around* an account. S6–S8 were written for a self-hosted
box where the only user **was** the operator, and three of their accepted tradeoffs stop being acceptable the
moment a second person has an account:

- **Signup is one shared secret.** `Bootstrap:Secret` gates `POST /auth/register`, so onboarding a colleague
  means handing them the operator's secret — which also gates `/auth/claim` and is the same string in `.env`.
  It cannot be scoped to one person, expired, revoked, or audited.
- **There is no operator surface, deliberately.** `AccountEndpoints`' XML doc says so outright: *"there is no
  role in this model that can see another user's devices, and an operator with shell access has `psql`."*
  That is true and fine for one user. With ten, the documented recovery for a forgotten password or a
  locked-out account is an operator opening `psql` against the volume, per incident.
- **The lockout's residual DoS was accepted on that basis.** `AuthEndpoints`' login doc acknowledges that
  anyone who knows an email can keep that account locked `Auth:LockoutMinutes` at a time, and accepts it
  because the locked-out user is the operator, who can go fix it. When they are not the operator, they are
  just stuck.

Plus one plain bug that only shows up with more than one user: the login rate limiter partitions on
`httpContext.Connection.RemoteIpAddress`, and `ForwardedHeaders` is (correctly) not enabled. Behind the
reverse proxy the README tells operators to run, **every user shares one 10-requests-per-minute bucket**.
Program.cs already carries the note explaining why the fix was not guessed at; S10 is where it gets made
explicit and opt-in.

S10 closes those four. It adds no new tenancy concept and changes no sync or report behaviour.

### The one architectural decision in this plan

Everything else here follows from **how an operator is identified**. Three options were weighed:

1. **Gate `/admin/*` on `X-Bootstrap-Secret` alone.** Rejected. It re-makes the shared secret a
   token-equivalent credential, which is exactly what S7 undid when it deleted `/auth/register-device`
   ("from S7 it can no longer mint a token at all — only a password can"). It also leaves admin actions with
   no identity to log, on the endpoints that most need an audit line.
2. **A `Role` enum on `User`.** Rejected as premature. There are two levels and no prospect of a third;
   an enum buys nothing over a bool and invites someone to invent `Auditor`.
3. **`User.IsAdmin`, a bool, enforced through the existing token machinery.** Chosen.

With (3) the admin surface rides everything S7 and S8 already built: an admin's access dies when their device
is removed, when their stamp is bumped, or when their account is deleted, with no new revocation path. And
**the flag is read from the database, never from the token.** `TokenRevocation.OnTokenValidatedAsync` already
loads the device row joined to its user on every authenticated request; `IsAdmin` rides along in the existing
`DeviceSnapshot` for free and is emitted as a claim onto the `ClaimsPrincipal` *after* validation. So a
demotion takes effect within `Auth:StampCacheSeconds` like every other revocation — and the demoting endpoint
evicts the key directly, so in practice it is immediate. Putting `is_admin` into the minted JWT instead would
give a year-long token a year-long privilege. Do not do that.

## Scope

### A. Invites

1. **`Invite` entity + migration.** `Id`, `TokenHash`, `NormalizedEmail?`, `CreatedByUserId`, `CreatedAt`,
   `ExpiresAtUtc`, `RedeemedAtUtc?`, `RedeemedByUserId?`, `Note?`. Unique index on `TokenHash`; index on
   `CreatedByUserId`. A null `NormalizedEmail` is an open invite (anyone may redeem it once); a set one binds
   the invite to that address and is compared with the same `ToUpperInvariant` normalization `User` uses.

   - **`TokenHash` is SHA-256 of the raw token, not PBKDF2, and that is deliberate.** An invite token is
     256 bits of `RandomNumberGenerator` output, not a human-chosen password: it has no guessable
     distribution to slow an attacker down over, so a work factor buys nothing. What it *costs* is the
     lookup — a salted per-row hash cannot be indexed, so redeeming would mean scanning every unredeemed
     invite and verifying each one. Write this on the entity so it is not "fixed" into PBKDF2 later.
   - **The raw token is returned exactly once**, from the creating call, and is never recoverable. Listing
     invites shows metadata only.
   - The generate / hash / constant-time-lookup / redeem logic lands here as a shared `SingleUseToken`
     helper, because the password resets in C reuse it verbatim (see item 11).
   - **The routes that manage invites are admin routes** and are listed with the rest of them in item 8.
     That makes B a prerequisite for part of A — see Sequencing below.
   - **Both user references are nullable, with `ON DELETE SET NULL`** — not the cascade every other table
     here uses. An invite is the record of how an account came to exist, so it has to outlive the admin
     who issued it *and* the user who redeemed it; cascading would destroy that record the first time
     either account was deleted. This is also what keeps the two account-deletion pipelines untouched:
     the constraint does the right thing without either of them learning about this table.
   - **`Auth:InviteExpiryDays`** (default 14) is the expiry an invite gets when the issuing admin does
     not choose one. There is deliberately **no value meaning "never"** — an invite that cannot expire is
     a standing credential, which is the thing invites exist to stop being.

2. **`POST /auth/register` accepts an invite token as an alternative to the shared secret.** Add
   `InviteToken` to `RegisterUserRequest` as an optional field (see the contract constraint below). The gate
   becomes: a valid `X-Bootstrap-Secret` **or** a valid unredeemed, unexpired invite. Keep the existing gate
   order and its distinct failures — `403` registration disabled, `401` neither credential accepted, `400`
   bad email or password, `409` email taken — and add `409` for an invite that is expired, already redeemed,
   or bound to a different address. That last one is frank for the same reason registration's duplicate-email
   `409` is: the caller already holds a one-time token addressed to them, so there is nothing to hide, and an
   opaque `401` would send them back to the person who sent it.
   - Redemption is part of the same transaction as the user insert. An invite consumed by a registration that
     then fails its unique-email check must not be burned.
   - Redeeming stamps `RedeemedAtUtc` and `RedeemedByUserId` rather than deleting the row — the audit trail
     is most of the point.

3. **`Auth:SignupSecretEnabled`** (bool, default `true`). When false, `X-Bootstrap-Secret` no longer satisfies
   `/auth/register` and an invite is the only way in. Default preserves every existing deployment exactly.
   **`/auth/claim` is not affected** — it is the pre-user-accounts upgrade path, it is already guarded by
   "exactly one user row in the database", and an operator who has turned signup off must still be able to
   claim their own data. (`UnclaimedUser` and `/auth/claim` are otherwise untouched by this plan. Retiring
   them is a contract-breaking removal for no functional gain; leave it to whichever release next breaks the
   wire on purpose.)

4. **`/auth/capabilities` stops lying.** `RegistrationRequiresSecret` is currently hardcoded `true` with a
   comment saying it is always true today. Compute it from `Auth:SignupSecretEnabled`, and add
   `RegistrationAcceptsInvite` (always `true` once this ships). Additive only — see the contract constraint.

### B. The operator surface

5. **`User.IsAdmin` (bool, default false) + `User.DisabledAtUtc` (timestamptz, null).** One migration with
   the `Invites` and `PasswordResets` tables.

6. **Who becomes the first admin.**
   - **On an empty database**, the user created by the first `POST /auth/register` is an admin. Deterministic,
     needs no seed and no config, and matches what "nothing is seeded; the first registration creates the
     first user" already promises. Two genuinely simultaneous first registrations can both see zero users and
     both come out admin; that is harmless on a fresh box and not worth a lock — note it, do not guard it.
   - **On an existing deployment**, the migration leaves every user non-admin, so the surface would be
     unreachable. `POST /admin/bootstrap` fixes that: **authenticated** (so the action has an actor to log)
     **and** gated on `X-Bootstrap-Secret` (which the operator has, in `.env`), it promotes the *calling*
     user to admin — and it is a `409` once any admin exists. Self-disarming, attributable, and it needs no
     `psql`. This is the only route in this plan that takes both a token and the secret.

7. **`IsAdmin` on the validated principal, not in the token.** Extend `TokenRevocation`'s `DeviceSnapshot` to
   `(UserId, SecurityStamp, IsAdmin, IsDisabled)`; the query already joins `Users`, so this is columns on an
   existing projection, not a second read. In `OnTokenValidatedAsync`:
   - Fail the token when `DisabledAtUtc` is set — disabling an account revokes every one of its tokens
     through the same path that already handles a deleted account, with no stamp bump needed.
   - Add an `admin` claim (`"true"`) to the principal when `IsAdmin`. Register an `"admin"` authorization
     policy requiring it, and put every `/admin/*` route behind `.RequireAuthorization("admin")`.
   - A non-admin hitting `/admin/*` gets `403`, not `404`. The routes are documented in the README; hiding
     them buys nothing and costs an operator a confusing session — the same reasoning
     `Auth:RegistrationEnabled` already uses for its `403`.

8. **Admin endpoints.** All authenticated, all under the `admin` policy, all addressing users by the internal
   `int Id`. **That is a deliberate exception to the SyncId rule in `CLAUDE.md`, and it should be stated in
   the XML doc**: the rule exists because a caller must not be able to widen their scope by guessing an id,
   and an admin's scope is already every user. There is no cross-device identity for a user to expose
   instead, and `GET /admin/users` hands out the ids anyway.

   | Route | Does |
   |---|---|
   | `GET /admin/users` | `AdminUserSummary[]`: id, email, displayName, createdAt, lastLoginUtc, deviceCount, isAdmin, isLocked, isDisabled. Ordered by id. |
   | `POST /admin/users/{id}/unlock` | Zeroes `FailedLoginCount`, clears `LockoutEndUtc`. `204`. The `psql` replacement. |
   | `POST /admin/users/{id}/disable` / `/enable` | Sets or clears `DisabledAtUtc`. Disabling evicts that user's cache keys so every token dies on the next request. `204`. |
   | `POST /admin/users/{id}/admin` / `DELETE …/admin` | Promote / demote. Demotion evicts the target's cache keys. `204`. |
   | `POST /admin/users/{id}/password-reset` | Mints a single-use reset token (C below) and returns it **once**. Does not set a password. |
   | `DELETE /admin/users/{id}` | The S8 deletion pipeline against another user. Body `AdminDeleteUserRequest(string ConfirmEmail)` matching the target's address (`400` otherwise). No password gate — the admin does not have it. |
   | `POST /admin/invites` | Body `InviteRequest(string? Email, int? ExpiresInDays, string? Note)`. Creates the invite from A and returns the **raw token once** in `InviteResponse`. Never recoverable afterwards. |
   | `GET /admin/invites` | `InviteSummary[]` — metadata only, never the token or its hash: who created it, the bound address if any, expiry, and whether and by whom it was redeemed. |
   | `DELETE /admin/invites/{id}` | Revoke an unredeemed invite. `204`; `409` for one already redeemed, because revoking it would falsify the audit trail the row exists to keep. |

9. **Self-protection and the last-admin guard.** Two guards, and they interact — **the second is
   unreachable unless the first is narrower than it first looks**:
   - **An admin cannot *suspend or delete* themselves through `/admin/*`.** `409`, and the delete case
     names `DELETE /auth/account`. These routes act on *other* people, and an admin who wants out has a
     self-service route that re-verifies their password.
   - **Demotion is the deliberate exception: an admin *may* aim it at themselves.** Standing down is an
     ordinary thing to want and, unlike the other two, has a safe outcome — you keep the account and
     lose only the role.
   - **The last admin cannot be demoted, disabled or deleted, by anyone.** `409`. Without it a
     deployment can be left with none, and the way back is not `/admin/bootstrap` — that is itself a
     `409` once any admin exists — but `psql`, which is the thing this plan removes. The check goes
     inside the same transaction as the write, over a
     `SELECT … WHERE IsAdmin AND DisabledAtUtc IS NULL FOR UPDATE`; a plain `COUNT` at read-committed
     lets two concurrent demotions each see two admins and both succeed.

   **Why demotion has to be the exception.** The caller of an admin route is always an *active* admin —
   the policy demands `IsAdmin`, and `TokenRevocation` refuses a suspended user's token outright. So if
   the self-action guard covered demotion too, the target would always be a second, distinct active
   admin, the count could never reach `1`, and **the last-admin guard would be dead code**. The
   invariant would still hold, but by accident, resting entirely on the self-guard and impossible to
   test. Letting an admin stand down is what gives the invariant a path to defend and a test to prove
   it.

10. **Every admin write logs one line** at information level: the acting user id, the action, and the target
    user id. **Ids, never emails** — for exactly the reason the S8 deletion line gives, and the reason the
    request log records only method/path/status.

### C. Password reset

11. **`PasswordReset` entity + a shared single-use-token helper.** `Id`, `TokenHash`, `UserId`, `CreatedAt`,
    `ExpiresAtUtc`, `RedeemedAtUtc?`, `CreatedByUserId`. Two tables rather than one polymorphic
    `AuthToken` with a `Purpose` discriminator: the FKs and the expiry policies genuinely differ, and a
    discriminator would mean half-null columns on every row. But the *crypto* lives once — **reuse the
    `SingleUseToken` helper built in item 1**, do not write a second one. Duplicating it is how one of the two
    ends up with a subtly weaker token.

12. **`POST /auth/password/reset`** — anonymous, rate-limited under `AuthEndpoints.RateLimitPolicy`. Body
    `ResetPasswordRequest(string ResetToken, string NewPassword)`. Validates the token (unredeemed,
    unexpired), applies the same password policy `/auth/register` and `/auth/password` apply **from the same
    `Credentials` accessor**, sets the hash, bumps `SecurityStamp`, clears the lockout counters, marks the
    token redeemed, and evicts the user's cache keys after the commit.
    - **Returns `204`, not a token.** Tokens are device-scoped and this call carries no `DeviceId`; every
      device signs in again, which is the correct outcome for a credential the user had lost control of.
    - An invalid, expired or redeemed token is a flat `400` with one message. This endpoint is anonymous, so
      distinguishing the three would turn it into an oracle — the same reasoning as login's uniform `401`.
    - Issuing a new reset for a user **invalidates that user's outstanding unredeemed resets**, so a token
      handed over twice does not leave two live doors.

13. **No email is sent, and that is the plan's known limit.** This server has no SMTP configuration and
    adding one is a dependency, a secret, and a deliverability problem, not a scope item. The admin hands the
    token over out of band. Self-service `POST /auth/password/forgot` is the follow-up that needs mail, and
    it is named in Out of scope so the gap is on the record rather than implied.

### D. The rate limiter behind a proxy

14. **Opt-in `ForwardedHeaders`.** New `Network:TrustedProxies` — a comma-separated list of proxy IPs or
    CIDRs, **unset by default**, in which case behaviour is byte-identical to today. When set, configure
    `ForwardedHeadersOptions` with those as `KnownProxies`/`KnownNetworks`, `ForwardedHeaders.XForwardedFor`
    only, and `ForwardLimit = 1`. Never a blanket `KnownNetworks.Clear()` — Program.cs' existing comment
    spells out why trusting `X-Forwarded-For` from an untrusted hop hands a caller an unlimited bucket per
    fake IP, and this must not quietly become that.
15. **Log one startup line** stating whether forwarded headers are trusted and from where. An operator
    debugging "everyone is getting 429" needs to see, in `docker compose logs`, which of the two modes the
    server is in.

### E. Contract, tests, docs

16. **Contract changes are additive only.** Per `CLAUDE.md`, `WorkLedger.Contracts` is vendored byte-identical
    into the client, so a change here is a two-repo change. Adding a field is safe — the client deserializes
    with default `System.Text.Json` options, which ignore unknown members — while renaming or removing one is
    not. So: `RegisterUserRequest` gains an optional `InviteToken`; `AuthCapabilitiesResponse` gains
    `RegistrationAcceptsInvite`; new records `InviteRequest`, `InviteResponse`, `InviteSummary`,
    `AdminUserSummary`, `AdminDeleteUserRequest`, `PasswordResetTokenResponse`, `ResetPasswordRequest`.
    **Nothing existing is renamed, reordered or removed.** `RegisterUserRequest` gaining a field changes its
    positional constructor, so the client's vendored copy must be re-taken before it next compiles against a
    new one — that is the handoff's job, not a reason to avoid the field.
17. **`plans/S10-client-handoff.md`**, in the shape of `S9-client-handoff.md`: the full inventory of added
    records and fields, the provenance commit to pin, and the statement that **the sync wire format is
    unchanged** — audited, not assumed. Per `plans/README.md`, this plan does **not** touch the client repo.
18. **Tests** (Testcontainers, alongside the existing `Auth/` suites):
    - Invites: an open invite registers a user and is marked redeemed; the same token a second time is `409`;
      an expired one is `409`; one bound to `a@x` redeemed with `b@x` is `409` and stays unredeemed; a
      registration that fails on a duplicate email leaves the invite unredeemed; with
      `Auth:SignupSecretEnabled=false` the shared secret alone is `401` and an invite still works; with it
      true (the default) the secret alone still works, unchanged.
    - First-admin: on an empty database the first registered user is admin and the second is not; on a
      database seeded with non-admin users `/admin/users` is `403`, `POST /admin/bootstrap` with the secret
      promotes the caller, and a second bootstrap is `409`.
    - Admin scope: every `/admin/*` route is `403` for a non-admin and `401` unauthenticated; a promoted
      user reaches them; **a demoted admin is refused on the very next request**, not after
      `Auth:StampCacheSeconds` — run at the default TTL, as the S8 suite does, so a dropped eviction fails.
    - Disable: a disabled user's existing token is refused immediately, their login answers the **same 401**
      as a wrong password (no new enumeration channel), and re-enabling restores both.
    - Last-admin: demoting, disabling or deleting the only admin is `409` and writes nothing; with two admins
      each succeeds. Self-disable, self-demote and self-delete through `/admin/*` are `409`.
    - Unlock: lock an account out via repeated failures, assert `423`, unlock it as admin, assert the correct
      password now works inside what would still have been the lockout window.
    - Reset: the issued token sets a new password and returns `204`; every pre-existing token on that user is
      then refused; the reset token is dead on the second use; an expired, unknown and already-redeemed token
      are the **same** `400`; issuing a second reset kills the first; a new password below
      `Auth:MinPasswordLength` is `400` and the old password still works.
    - `DELETE /admin/users/{id}`: wrong `ConfirmEmail` is `400` and nothing is deleted; the happy path empties
      all five tables for that user, refuses their tokens, and **leaves a second user's data untouched**.
    - Admin routes reject a user id belonging to nobody with `404`.
19. **Docs and deployment.** README: a "Running this for more than one person" section (bootstrap the first
    admin, issue an invite, hand over a reset token, the admin route table), `Auth__SignupSecretEnabled` and
    `Network__TrustedProxies` in the config table, and the proxy note pointed at item 14 instead of at the
    apology it currently makes. `.env.example` gains both keys, commented, with their defaults. Extend
    `scripts/smoke-test.ps1` with a second user registered via an invite issued by the first, proving the two
    accounts' data does not mix through the container.

## Sequencing

The scope groups are lettered by subject, **not by build order**. Build **B before A**: the invite management
routes in item 8 sit behind the `admin` policy that B establishes, so A1/A2 (the entity and the registration
gate) can land first but A3's routes cannot. Then **A before C**, because C reuses A's `SingleUseToken`
helper. **D is independent** of all of them and can go in parallel or first. **E is last** and depends on
everything, exactly as S9 did — re-vendoring the contract while it is still moving is the mistake S6–S8
already made once.


## Out of scope

- **Per-(email, IP) failure counting.** The lockout DoS named in Context is only *partly* closed here: admin
  unlock downgrades it from "the operator opens `psql`" to "ask an admin", but anyone who knows an address can
  still burn it down again. The real fix is partitioning the failure counter by source IP so one attacker
  cannot lock a legitimate user on a different address, which needs a failure table and an eviction policy.
  Say so in the login XML doc rather than implying S10 solved it.
- **Self-service `POST /auth/password/forgot`.** Needs SMTP — see item 13.
- **Email verification on registration.** Same dependency, and an invite already proves an operator vouched
  for the address.
- **Per-user quotas.** One user can still fill the volume for everyone. Worth a plan; not this one.
- **Shared workspaces / teams.** Multiple users over *one* dataset is a different model entirely — a
  workspace row between `User` and the data, a membership table, and re-scoping every sync and report query
  off `UserId`. S6 considered and rejected a `User`→`Account` 1:N split; reversing that is a wire-breaking
  release of its own, not an extension of this one.
- **Retiring `/auth/claim` and `UnclaimedUser`** — see item 3.
- **An admin UI, and any client change.** Item 17 hands the client an inventory; the client repo is edited as
  client-repo work.
- **Audit log table.** Item 10 logs to the request log. A queryable audit trail is a real feature and a
  separate one.

## Acceptance criteria

- A second person can be onboarded without ever seeing `Bootstrap__Secret`: an admin issues an invite, they
  register with it, they log in from two devices, and their data is invisible to every other user.
- With `Auth__SignupSecretEnabled=false`, an invite is the only way to create an account, and `/auth/claim`
  still works.
- Every existing deployment behaves identically after this migration until an operator turns something on:
  the shared secret still registers, the rate limiter still partitions on the socket address, and no user is
  an admin until `POST /admin/bootstrap` is called.
- An operator can list users, unlock, disable, promote, demote, issue a password reset and delete an account
  without touching `psql`.
- Demoting, disabling or deleting an admin takes effect on that user's **very next request**, and the
  deployment can never be left with zero admins.
- A user who has forgotten their password gets back in with a single-use token that dies on first use,
  signs out all their other devices, and is indistinguishable from an expired or unknown token when wrong.
- No route added here reads a *tenancy* scope from a request body or URL: `/admin/*` takes target ids by
  design and is gated on a database-resolved `IsAdmin`; everything else still scopes on the token alone.
- No sync or report behaviour changed. The merge rules in `SyncService` and the counting rules in
  `ReportService` are untouched, and `S10-client-handoff.md` says so on the strength of an audit.
- Full suite green under Testcontainers, and `scripts/smoke-test.ps1` green against the composed stack.

## Suggested commit message

```
feat(auth): add invites, an operator surface and password reset
```

## Prompt for Claude Code

> Read `CLAUDE.md`, `plans/S8-account-deletion-and-device-management.md` and this plan first. **Do not add
> multi-user support — S6–S9 already shipped it**; verify that (one `User` row owns many `Device` rows, unique
> on `(UserId, DeviceId)`, every sync and report query filtered on the `user_id` claim) and then build the
> operations layer around it.
>
> Add an `Invites` table and a `PasswordResets` table, both keyed on a SHA-256 `TokenHash` of a 256-bit
> random token with a unique index — **not** PBKDF2, because the token is high-entropy and a salted hash
> cannot be indexed; document that on the entity. Share the generate/hash/lookup/redeem logic in one
> `SingleUseToken` helper. Add `User.IsAdmin` and `User.DisabledAtUtc` in the same migration.
>
> Make `POST /auth/register` accept a valid invite token **or** the existing `X-Bootstrap-Secret`, redeeming
> the invite in the same transaction as the user insert so a failed registration does not burn it, and add
> `Auth:SignupSecretEnabled` (default true) to let an operator switch the shared secret off. The first user
> created on an empty database is an admin; on an existing one, `POST /admin/bootstrap` — authenticated **and**
> carrying the secret — promotes the caller and is a `409` once any admin exists.
>
> Resolve admin status from the **database, never from the JWT**: widen `TokenRevocation`'s `DeviceSnapshot`
> to carry `IsAdmin` and `DisabledAtUtc` (the query already joins `Users`), fail the token for a disabled
> user, and emit an `admin` claim onto the validated principal so an `"admin"` authorization policy can gate
> `/admin/*`. Build `GET /admin/users`, unlock, disable/enable, promote/demote, `POST
> /admin/users/{id}/password-reset` returning a single-use token once, and `DELETE /admin/users/{id}` reusing
> S8's children-first `ExecuteDeleteAsync` transaction with a typed `ConfirmEmail` gate. Every write evicts
> the affected `TokenRevocation` cache keys **after** the commit, and logs one line with the actor id, the
> action and the target id — ids, never emails. An admin cannot disable, demote or delete themselves through
> `/admin/*`, and the last admin cannot be demoted, disabled or deleted by anyone; count the remaining admins
> inside the same transaction as the write.
>
> Add anonymous, rate-limited `POST /auth/password/reset` taking the token and a new password, applying the
> same policy through the same `Credentials` accessor, bumping `SecurityStamp`, clearing the lockout counters
> and returning `204` with no token; an unknown, expired or redeemed token is the same flat `400`, and issuing
> a reset invalidates that user's outstanding ones. Add opt-in `Network:TrustedProxies` wiring
> `ForwardedHeaders` with `XForwardedFor` only and `ForwardLimit = 1` — never `KnownNetworks.Clear()` — unset
> by default so today's behaviour is unchanged, and log at startup which mode is active.
>
> Keep every `WorkLedger.Contracts` change **additive**: an optional `InviteToken` on `RegisterUserRequest`,
> `RegistrationAcceptsInvite` on `AuthCapabilitiesResponse`, and new records for the invite, admin and reset
> payloads. Rename and remove nothing. Write Testcontainers tests for each bullet under scope item 18, running
> at the **default** `Auth:StampCacheSeconds` so a missing cache eviction fails the suite. Then update
> `README.md`, `.env.example` and `scripts/smoke-test.ps1`, and write `plans/S10-client-handoff.md` in the
> shape of `S9-client-handoff.md`, stating on the strength of a file-by-file audit that the sync wire format
> is unchanged. **Do not modify the client repo.**
