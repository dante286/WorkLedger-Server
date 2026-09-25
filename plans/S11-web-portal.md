# Plan S11: A web portal for accounts and operators

**Tickets:** epic **WL-179** (*Web portal: self-service accounts and an operator UI (server)*), with one
`Task` per scope group and `Subtask`s per item, labelled `server` + `auth` + `web`. The client-side half is
the rewritten client **plan 12** (`WorkLedger-Client/plans/12-account-lifecycle-to-web-portal.md`, epic
WL-173), which this plan hands off to through `plans/S11-client-handoff.md` (item 24). WL-185 blocks WL-173.

| Group | Items | Task | Subtasks |
|---|---|---|---|
| A. Service layer | 1–4 | WL-180 | WL-186 `CredentialCheck`, WL-187 `AccountDeletion`, WL-188 `AdminOperations`, WL-189 invite/reset/registration/self-service |
| B. Portal foundation | 5–11 | WL-181 | WL-190 hosting + config, WL-191 cookie scheme + session validation, WL-192 Data Protection, WL-193 headers + log hygiene, WL-194 login/logout |
| C. Self-service account pages | 12–15 | WL-182 | WL-195 home + devices, WL-196 change password + sign out everywhere, WL-197 delete account |
| D. Operator pages | 16–18 | WL-183 | WL-198 users, WL-199 invites, WL-200 reset issuance |
| E. Link-driven onboarding | 19–21 | WL-184 | WL-201 setup, WL-202 register, WL-203 reset redemption |
| F. Contract, compatibility, tests, docs | 22–27 | WL-185 | WL-204 contract, WL-205 compatibility routes, WL-206 tests, WL-207 docs, WL-208 client handoff |

Build order is **A → B → D → E → C → F**, *not* the lettering. See Sequencing.

## Context

**S10 built the operations layer. S11 gives it a front end, and it moves account management off the client.**
After S10 an operator can do everything without `psql`, but only through `curl` against `/admin/*`. Regular
users can manage their account only through the desktop client (client plans 9 and 10), and the S10 client
work that was drafted (the original client plan 12) would have added invite tokens and password-reset
redemption to the desktop app as well.

The architecture has changed direction (decided 2026-09-25):

- **The client is the user's time-tracking tool and nothing more.** It works fully offline (Off / File sync),
  and when pointed at a server it signs in with an existing account. It does **no** account lifecycle:
  no registration, no claim, no password change or reset, no device management, no account deletion.
- **The server owns every account operation**, through a web portal served by the same process. Regular users
  sign in to manage their own account; admins also manage other users and invites.
- **Each side works without the other.** The client is useful with no server at all. The server, and its
  portal, is useful with no client: an operator can stand one up, onboard people and manage them before
  anyone installs the desktop app.

The server-side consequence is not "add some pages". **The auth model has no concept of a browser session.**
S7 made `POST /auth/login` the only token source, every token is scoped to a `(user, device)` pair and lives
`Jwt:ExpiryDays` (365), and revocation is a device row joined to a user row. A web portal cannot reuse that
without putting a year-long bearer credential in a browser and a "Chrome on someone's laptop" row in every
user's device list. So this plan's one architectural decision is how a browser authenticates.

### The one architectural decision in this plan

Three options were weighed:

1. **The portal is a device.** It logs in with a synthetic `DeviceId` and keeps the JWT in browser storage.
   Rejected. It pollutes `GET /auth/devices` and the admin device counts, it puts a 365-day bearer token where
   any XSS can read it, and it makes "sign this browser out" mean deleting a device row.
2. **A separate front end (SPA, or a second service) calling the JSON API.** Rejected for now. It needs a
   token strategy anyway (option 1's problem again, or a new short-lived token type), it adds a Node toolchain
   to a .NET repo, and a second process breaks immediate revocation. **`TokenRevocation`'s cache is an
   in-process `IMemoryCache`**, and the endpoints that revoke evict it directly. An admin UI running in another
   process would demote, disable or delete users without evicting the API's cache, so every revocation would
   quietly go back to lagging `Auth:StampCacheSeconds`.
3. **Server-rendered pages in the same process, authenticated by a cookie scheme alongside the JWT one.**
   Chosen.

With (3) a portal session is **user-level, short-lived, and validated against the same user row** that
already carries `SecurityStamp`, `IsAdmin` and `DisabledAtUtc`. It rides the revocation machinery S7, S8 and
S10 built (a stamp bump, a suspension, a demotion or a deletion all end it on the next request) and needs no
`Device` row. The two schemes are **kept apart by route, never merged**: the JSON API accepts bearer tokens
only, and the portal accepts the cookie only. A cookie that could reach `/admin/*` or `/sync/*` would make the
JSON API vulnerable to cross-site request forgery, since nothing there checks antiforgery tokens. A bearer
token that could reach a portal page would bypass the portal's antiforgery and session rules.

**Rendering: Razor Pages**, in the existing `WorkLedger.Server` project (it is already `Microsoft.NET.Sdk.Web`,
so no new SDK or package is needed for pages). Blazor Server was rejected because it needs a websocket through
the operator's reverse proxy, which README deployments are not guaranteed to pass. Blazor static SSR would also
work but brings a component model this surface does not need. Razor Pages gives built-in antiforgery, plain
HTML forms that work without JavaScript, and nothing to build.

## Scope

### A. Extract a service layer (no behaviour change)

The rules this portal must obey currently live **inside endpoint lambdas**. `AdminEndpoints.cs` is about 800
lines, and the children-first deletion pipeline is written twice, once in `AccountEndpoints` (`DELETE
/auth/account`) and once in `AdminEndpoints` (`DELETE /admin/users/{id}`). If the portal calls into the
endpoints' logic by copying it, a third copy of the last-admin guard, the cache eviction or the lockout rules
is the result, and one of them will drift. So group A moves logic, not behaviour. **Every existing test stays
green without modification**; that is the acceptance criterion for the whole group.

1. **`CredentialCheck`.** The login verification from `POST /auth/login`: normalized lookup, the uniform
   failure (unknown email, wrong password and credential-less row are indistinguishable), suspension answering
   the same as a wrong password, the lockout counter, **the counter resetting when the window expires**, and
   **a 423 writing nothing at all**. It returns an outcome (`Verified(user)`, `Invalid`, `LockedOut(until)`)
   and does not mint anything. `POST /auth/login` becomes "check, then upsert the device and mint";
   the portal login (item 10) becomes "check, then sign in the cookie". Both callers therefore spend
   **the same per-user failure budget**, which is correct: it is one account and one password.
2. **`AccountDeletion.DeleteUserAsync(userId)`.** The one children-first, one-transaction,
   `ExecuteDeleteAsync`-per-table pipeline with its per-table counts and its single ids-only warning line.
   `DELETE /auth/account` and `DELETE /admin/users/{id}` both call it; their gates (password + typed email,
   and typed email + admin + guards) stay in the callers. Evicting the user's revocation keys moves in with it.
3. **`AdminOperations`.** Unlock, disable/enable, promote/demote, delete, issue reset; the self-action guard,
   the last-admin guard with its `FOR UPDATE` count inside the write's transaction, eviction after commit, and
   the audit line (actor id, action, target id, never emails). Each method returns an outcome enum. The
   `/admin/*` handlers map outcomes to status codes; the portal maps the same outcomes to page messages.
   `POST /admin/bootstrap`'s promotion logic moves here too, because item 19 reuses it.
4. **`InviteService`, `PasswordResetService`, `RegistrationService`, `AccountSelfService`.** Issue / list /
   revoke / redeem invites (over the existing `SingleUseToken`); issue a reset (invalidating the user's
   outstanding ones) and redeem one (policy check through `Credentials`, stamp bump, lockout cleared, evict
   after commit); register (secret-or-invite gate, first-user-is-admin, invite redeemed in the same
   transaction as the insert) and claim; and the S8 self-service set (list/remove devices, change password,
   sign out everywhere).
   - **Keep the XML docs with the rules.** House convention puts a non-obvious rule on the type that owns it.
     When a rule moves from an endpoint class into a service, its doc moves with it, and the endpoint's doc
     shrinks to "see `X`". Do not leave the reasoning on a lambda that no longer contains the logic.

### B. Portal foundation

5. **Hosting and configuration.**
   - `Web:Enabled` (bool, **default `false`**). With it off, no page, static asset or cookie middleware is
     mapped and the server is byte-identical to S10 for every existing request. This follows S10's rule that
     an existing deployment behaves the same after an upgrade until the operator turns something on.
     Publishing a new login form on an internet-facing box is exactly that kind of change.
   - `Web:PublicBaseUrl` (absolute `https://` URL, e.g. `https://ledger.example.com`). **Required when
     `Web:Enabled` is true**, and resolved eagerly after `Build()` alongside the existing fail-fast checks, so a
     missing or relative value exits the container naming the key. It is the only source for absolute links
     (invite and reset links, and the capability in item 22). **Never build a link from the request's `Host`
     header**: behind a proxy it is whatever the caller sent. An `http://` value is refused unless the host is
     `localhost`, for development.
   - `Web:SessionHours` (default 8): the cookie's sliding lifetime. There is deliberately no "remember me".
     A portal session is for a task (issuing an invite, removing a device), not for staying signed in.
   - Pages live under **`/portal`**. `/admin/*` is taken by the JSON API and must not move (it is documented
     and scripted against), and a distinct prefix is what lets an operator restrict the portal at the proxy if
     they want to. Suggested layout: `/portal/login`, `/portal/logout`, `/portal` (account home),
     `/portal/account/*`, `/portal/admin/*`, `/portal/setup`, `/portal/register`, `/portal/reset`.
   - **`GET /` content-negotiates** when the portal is on: a request whose `Accept` includes `text/html`
     redirects to `/portal`; anything else gets today's JSON name/version. With the portal off, `/` is
     unchanged. The existing root and health tests must pass untouched.

6. **A cookie authentication scheme, separate from the bearer scheme.**
   - Add a named cookie scheme (`"Portal"`); **the default scheme stays `JwtBearer`**. The existing
     `FallbackPolicy` and `TokenRevocation.AdminPolicy` therefore still authenticate by bearer only, and every
     JSON route is unchanged.
   - Two new policies, `PortalUser` and `PortalAdmin`, each built with `.AddAuthenticationSchemes("Portal")`
     so they never consult the bearer scheme. **Every portal page carries one of them or an explicit
     `AllowAnonymous`**, applied by Razor Pages conventions (`AuthorizeFolder`, `AllowAnonymousToPage`).
     A page with neither falls to the fallback policy, which authenticates by bearer, so it fails closed
     rather than open. Keep it that way.
   - Cookie: `__Host-WorkLedger` (the prefix forces `Secure`, `Path=/` and no `Domain`), `HttpOnly`,
     `SameSite=Lax`, `SecurePolicy = Always`, sliding expiry from `Web:SessionHours`. Login, logout, access
     denied and challenge paths all point into `/portal`, and **challenges on `/portal/*` redirect to the login
     page, never 401**. The JSON API's challenges keep returning 401; a redirect there would break the client.
   - **Why `SecurePolicy = Always` and not trusted `X-Forwarded-Proto`.** S10 deliberately trusts
     `XForwardedFor` only, and the comment in Program.cs explains why accepting proto headers invites redirect
     loops. The portal does not need to change that. A `Secure` cookie is accepted by the browser whenever the
     *browser's* connection is HTTPS, whatever the proxy-to-app hop was, and every absolute URL comes from
     `Web:PublicBaseUrl` instead of the request scheme. Browsers treat `http://localhost` as a secure context,
     so development works too. **Do not widen `ForwardedHeaders` in this plan.**

7. **Session validation against the user row.** The cookie carries `user_id` and `stamp`, and **no `admin`
   claim is stored in it**, for the same reason S10 keeps admin out of the JWT: it would outlive a demotion.
   `CookieAuthenticationEvents.OnValidatePrincipal` loads a per-user snapshot `(SecurityStamp, IsAdmin,
   IsDisabled)`, cached under **`user:{id}`** for `Auth:StampCacheSeconds`, and:
   - rejects the principal and signs the cookie out when the user is gone, suspended, or the stamp differs;
   - adds `TokenRevocation.AdminClaim` to the principal when `IsAdmin`, exactly as `TokenRevocation` does for
     bearer requests.
   - **`TokenRevocation.EvictUserAsync` evicts `user:{id}` as well as the user's `device:{rowId}` keys.**
     Every revoking path already calls it after commit, so this one line is what makes a demotion, a suspension
     or a password change end a portal session on the next request rather than after the cache TTL. The S10
     tests run at the default TTL for this reason; the portal tests in item 25 must too.
   - Portal logout clears the cookie **and bumps nothing**. Signing out of a browser must not sign out every
     desktop install.

8. **Data Protection keys persist in Postgres.** Cookie encryption and antiforgery tokens both depend on
   ASP.NET Data Protection, whose default key ring lives in the container's filesystem. Every image upgrade or
   restart would then sign everyone out and fail any form in flight, and a second replica could never read the
   first one's cookies.
   - `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`: `WorkLedgerDbContext` implements
     `IDataProtectionKeyContext`, a migration adds `DataProtectionKeys`, and
     `.PersistKeysToDbContext<WorkLedgerDbContext>().SetApplicationName("WorkLedger")`. The application name
     is what keeps keys valid when the content root path differs between images.
   - The keys sit unencrypted at rest in the same database. That is acceptable and should be written down:
     anyone who can read that table can already read every user row.
   - Registered only when `Web:Enabled`, so the migration adds a table that an S10-shaped deployment never
     reads.

9. **Response hardening on portal responses** (and only there; the JSON API's responses are unchanged):
   `Content-Security-Policy: default-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none';
   object-src 'none'`, `Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff`, and `Cache-Control:
   no-store` on any page that renders a token or account detail. **No inline script**, so the CSP needs no
   `unsafe-inline`; the one script item 19 needs is a static file. Static assets are served from
   `wwwroot` via `MapStaticAssets()`, mapped only when the portal is on. The desktop client's brand
   (`WorkLedger-Client/docs/workledger-brand.html`) can supply colors and type, but a plain, readable
   stylesheet is enough for this plan.
   - **Dates.** The runtime image runs invariant globalization (see `CLAUDE.md`), so server-side formatting
     cannot follow a user's locale. Render timestamps as ISO-8601 UTC inside `<time datetime="…">` and let a
     small static script localize them. Without JavaScript the page still shows correct UTC.

10. **Portal login and logout.**
    - `/portal/login`: email + password, antiforgery, rate-limited under `AuthEndpoints.RateLimitPolicy` (the
      same per-IP bucket as `/auth/login`). It calls `CredentialCheck` (item 1), so the uniform failure, the
      lockout and the no-write 423 behave exactly as they do for a device. A lockout shows "try again in about
      N minutes" from the outcome, never a remaining-attempts count.
    - On success: sign in the `Portal` cookie; **no `Device` row is created or touched**. `User.LastLoginUtc`
      *is* updated, because it is a user-level fact about signing in and the admin list shows it. Record in its
      XML doc that it now covers both kinds of sign-in.
    - Redirect only to a local `returnUrl` (`LocalRedirect` / `Url.IsLocalUrl`). An open redirect on a login
      page is a phishing kit.
    - `/portal/logout` is a POST with antiforgery (a GET logout can be triggered by an `<img>` tag).

11. **Request log hygiene.** The request log records `Request.Path`, which excludes the query string. S11 adds
    the first flows where a secret arrives in something other than a header (invite and reset tokens), so
    **no token may ever be in a path segment**. Items 19–21 pass tokens in the URL fragment or a POST body
    only. Add a test (item 25) that captures log output across the invite and reset flows and asserts the raw
    token never appears.

### C. Self-service account pages (`PortalUser`)

12. **Account home (`/portal`).** Email, display name, created date, and links to the pages below. For an
    admin, a link to `/portal/admin`.
13. **Devices (`/portal/account/devices`).** The S8 list (label, first seen, last seen) via
    `AccountSelfService`, with a Remove button per device (POST + antiforgery). There is no "current device"
    marker: a browser is not a device, which is the point of item 6. Removal revokes that install's token and
    leaves every synced row alone, as S8 specifies. The page should say so, because users will assume removing
    a device deletes its data.
14. **Change password and sign out everywhere (`/portal/account/security`).**
    - Change password: current + new + confirm, the `Credentials` policy, and a stamp bump, so **every desktop
      install is signed out**. The page says so before submitting. The portal session survives: after the
      commit, re-issue the cookie with the new stamp. Otherwise the user's own next click would bounce them to
      the login page for the thing they just did.
    - Sign out everywhere: a stamp bump, same re-issue, same warning.
    - Both run under the per-user lockout's sibling rule from S8: a wrong current password is a page message,
      not a sign-out.
15. **Delete account (`/portal/account/delete`).** Current password + typed email, the same two independent
    gates as `DELETE /auth/account`, through `AccountDeletion` (item 2). On success, sign the cookie out and
    render a static "your account and all its synced data have been deleted" page.
    - **The sole-admin case matches `DELETE /auth/account`:** allowed. S10 recorded why (guarding it would make
      the last account on a server undeletable, and `/admin/bootstrap` / `/portal/setup` start answering again
      once no admin exists). The page warns an admin who is the last one before they confirm.
    - Desktop installs signed in to the deleted account discover it as a 401 on their next sync. Client plan 12
      handles what that looks like.

### D. Operator pages (`PortalAdmin`)

16. **Users (`/portal/admin/users`).** The `GET /admin/users` data as a table, with per-row actions: unlock,
    disable/enable, promote/demote, issue password reset, delete. Every action is a POST with antiforgery and
    goes through `AdminOperations`, so the self-action guard, the last-admin guard and the audit line are the
    same code the JSON routes run. Delete asks for the target's email typed out, as `AdminDeleteUserRequest`
    does.
    - Guard refusals render the same reason the JSON route's `409` body gives.
    - **Demoting yourself** is allowed (S10 item 9). The page then redirects to `/portal`, because the admin
      pages will refuse the next request. That is the policy working, not a bug.
17. **Invites (`/portal/admin/invites`).** Create (optional bound email, expiry in days defaulting to
    `Auth:InviteExpiryDays`, optional note), list (metadata only, as `GET /admin/invites`), revoke unredeemed.
    - **Creating shows the link exactly once:** `{PublicBaseUrl}/portal/register#invite={token}`, with a copy
      button and `Cache-Control: no-store`. It is never recoverable afterwards, as with the JSON route. The page
      says so, and says the link is single-use.
18. **Issue a password reset** (from the users table). Shows `{PublicBaseUrl}/portal/reset#token={token}` once,
    with its expiry (`Auth:PasswordResetExpiryHours`). Issuing a new one kills that user's outstanding resets,
    as S10 specifies; the page states it.
    - **No email is sent**, which is S10's standing limit. The admin passes the link on by whatever channel
      they use. Self-service "forgot password" still needs SMTP and stays out of scope.

### E. Link-driven onboarding (anonymous pages)

These three pages share one rule: **loading a page never spends a token.** Chat and mail clients fetch links
to build previews, and a `GET` that redeemed an invite would burn it before the recipient ever clicked.
Tokens travel in the **URL fragment** (`#invite=…`, `#token=…`). Fragments are never sent to the server, never
reach the request log or the proxy's access log, and never leak in a `Referer`. A small static script (item 9)
copies the fragment into a hidden form field, and every page also has a visible **paste-the-token field**, so
the flow works without JavaScript and when a link has been mangled in transit. Redemption happens only on the
POST. All three are rate-limited under `AuthEndpoints.RateLimitPolicy` and carry antiforgery.

19. **`/portal/setup`: first run, claim and first admin.** One page that knows the server's bootstrap state
    and offers the one action that state allows. **Every branch requires `Bootstrap:Secret`**, because the
    first person to find a fresh server would otherwise own it.

    | State | Page offers | Does |
    |---|---|---|
    | Zero users | Create the first account | `RegistrationService` register via the secret; first user is admin (S10 item 6); then signs the cookie in |
    | One credential-less pre-S6 row | Claim this server | `/auth/claim`'s logic; `Auth:SignupSecretEnabled` is ignored here as S10 item 3 requires |
    | Users exist, no admin | Sign in and promote yourself | `CredentialCheck` + `AdminOperations` bootstrap (the `/admin/bootstrap` rule) |
    | An admin exists | Nothing: "this server is set up" | `404`, so the page does not advertise the secret prompt |

    This replaces registering the first account from the desktop client, which client plan 12 removes.
20. **`/portal/register`: accept an invite.** Invite token (from the fragment or pasted), email, display name,
    password + confirm. Through `RegistrationService`, which keeps S10's gate: a valid unredeemed, unexpired
    invite bound to this address (or open), redeemed in the same transaction as the insert, so a registration
    that fails its unique-email check leaves the invite unspent. An expired, spent or wrongly-addressed invite
    shows the server's own sentence, exactly as S10 intended when it chose a frank `409` for that case.
    - When `Auth:SignupSecretEnabled` is true the form also accepts the signup secret instead of a token, so a
      deployment that has not moved to invites can still onboard through the portal.
    - When `Auth:RegistrationEnabled` is false the page says registration is closed (the `403` equivalent).
    - Success signs the new user straight in to the portal and shows the server URL to paste into the desktop
      client. That handoff from web to client is the whole onboarding story now.
21. **`/portal/reset`: redeem a password reset.** Token (fragment or pasted), new password + confirm, through
    `PasswordResetService`. That means the `Credentials` policy, a stamp bump, cleared lockout counters,
    eviction after commit, and **the same single message for an unknown, expired or spent token**, as S10 made
    `POST /auth/password/reset` answer. A too-short password leaves the token spendable, and the page says so.
    - Success shows: "Your password has been changed. Every device has been signed out; sign in again on each
      one." It links to `/portal/login` but **does not sign the user in**, because the reset proves possession
      of a link, and the next sign-in proves the password.

### F. Contract, compatibility, tests, docs

22. **Contract: one additive field, and two records leave the client-facing library.**
    - `AuthCapabilitiesResponse` gains a **trailing, defaulted** `string? PortalUrl = null`:
      `{Web:PublicBaseUrl}/portal` when the portal is on, `null` when it is off. That is how the client finds the
      portal without guessing at paths (client plan 12). Additive, last, defaulted, so the client's vendored copy
      keeps compiling, exactly as S10's two fields did.
    - **Move `Auth/AdminDtos.cs` and `ResetPasswordRequest` out of `WorkLedger.Contracts`** into
      `WorkLedger.Server` (e.g. `Auth/AdminDtos.cs`, namespace `WorkLedger.Server.Auth`). This looks like a
      removal and is not a break: **the client's vendored copy is pinned at `3d27c86`, which predates both**, and
      the rewritten client plan 12 will never call `/admin/*` or `/auth/password/reset`. A record no client has
      ever compiled against can move without breaking anyone. The rule this establishes, and `plans/README.md`
      should state it: **`WorkLedger.Contracts` holds what the desktop client speaks and nothing else.** The
      admin JSON API's shapes are server-internal.
    - **Everything the client *has* vendored stays exactly where it is**, including the records client plan 12
      stops using (`RegisterUserRequest`, `ChangePasswordRequest`, `DeviceSummary`, `DeleteAccountRequest`).
      Removing them is a wire-breaking change for any v0.x client still in the field, and belongs to whichever
      release next breaks the wire on purpose. List them in the handoff as "server-only once client plan 12
      ships; remove at the next breaking release".
    - `Sync/`, `Payloads/` and `Reports/` are untouched. Audit with `git diff 1e343c8..HEAD --
      src/WorkLedger.Contracts/Sync src/WorkLedger.Contracts/Payloads src/WorkLedger.Contracts/Reports`
      (expected empty) and say so in the handoff.

23. **The JSON account routes become compatibility routes, and stay.** `POST /auth/register`,
    `POST /auth/claim`, `POST /auth/password`, `GET`/`DELETE /auth/devices`, `POST /auth/logout-all`,
    `DELETE /auth/account` and `POST /auth/password/reset` keep working unchanged, now thin callers of the
    group-A services, so v0.1.x clients in the field and existing `curl` scripts are unaffected. Mark each one's
    XML doc as a compatibility route: **no new behaviour lands on them**, and new account features are
    portal-only. `/admin/*` is **not** a compatibility route: it stays the scripting interface for operators,
    documented in the README beside the portal.

24. **`plans/S11-client-handoff.md`**, in the shape of `S10-client-handoff.md`: the pin to record (the last
    commit to touch `src/WorkLedger.Contracts/`), the one added field, the two moved records and why they cost
    the client nothing, the list of compatibility routes and the records they keep alive, the sync audit, and a
    short "what the client does now" pointing at client plan 12. **Note that the client never re-vendored at
    S10's `1e343c8`**: this handoff covers S10's two trailing fields as well, and supersedes
    `S10-client-handoff.md`'s step 0. Per `plans/README.md`, this plan does not touch the client repo.

25. **Tests** (Testcontainers, alongside the existing suites; portal tests use `WebApplicationFactory` with
    `ClientOptions.BaseAddress = https://localhost` so the `__Host-` cookie round-trips, and parse the
    antiforgery token from the rendered form):
    - **Group A:** the full existing suite passes with **no test modified**. Any test that needed changing is
      a behaviour change and should be called out in the PR.
    - **Scheme separation:** a valid portal cookie against `GET /auth/devices`, `POST /sync/push` and
      `GET /admin/users` is `401`; a valid bearer token against `/portal`, `/portal/account/devices` and
      `/portal/admin/users` redirects to login; an unauthenticated JSON request still gets `401`, not a redirect.
    - **Portal off (default):** `/portal/*` is `404`, `GET /` returns JSON for any `Accept`, no Data Protection
      or cookie middleware is registered, and `AuthCapabilitiesResponse.PortalUrl` is `null`.
      `Web:Enabled=true` without `Web:PublicBaseUrl` fails startup naming the key.
    - **Login:** success sets the cookie and creates **no** `Device` row; wrong password and unknown email
      render the same message; five portal failures lock the account for `/auth/login` too (and vice versa),
      and a portal attempt while locked writes nothing; a disabled user sees the wrong-password message; an
      off-site `returnUrl` is ignored.
    - **Revocation, at the default `Auth:StampCacheSeconds`:** `POST /auth/logout-all` from a device ends the
      portal session on the next request; admin disable ends it on the next request; a demoted admin's next
      `/portal/admin/*` request is refused; portal logout leaves the device's bearer token working.
    - **Self-service:** changing the password in the portal kills every bearer token and keeps the portal
      session; removing a device kills that device's token and leaves its synced rows; deleting the account
      empties all five tables for that user and leaves a second user untouched.
    - **Antiforgery:** every portal POST without the token is `400` and writes nothing.
    - **Onboarding:** `GET /portal/register` and `GET /portal/reset` write nothing whatever the URL carries;
      a registration failing on a duplicate email leaves the invite unredeemed; the invite link's token
      registers exactly once; a reset link's token works once, its second use and an unknown token show the
      same message, and bearer tokens issued before it are refused.
    - **Setup:** each of the four states in item 19 offers only its own action; a wrong secret is refused in
      every branch; the page is `404` once an admin exists.
    - **Token hygiene:** with log capture across invite creation, registration, reset issuance and redemption,
      no raw token appears in any log line.
    - **Keys:** two factories over the same database: a cookie issued by the first is accepted by the second
      (the Data Protection key ring is shared through Postgres, not the filesystem).
    - **Headers:** portal responses carry the CSP, `Referrer-Policy` and `nosniff`; JSON responses are
      unchanged.
    - **Capabilities:** `PortalUrl` is `{PublicBaseUrl}/portal` when on; the seven existing fields are
      unchanged.

26. **Docs and deployment.**
    - README: a "Web portal" section covering enabling it, `Web__PublicBaseUrl`, putting it behind the TLS
      proxy, first run through `/portal/setup`, and onboarding by invite link. "Running this for more than one
      person" is rewritten portal-first, with the `curl` flow kept as the scripting alternative. Add
      `Web__Enabled`, `Web__PublicBaseUrl` and `Web__SessionHours` to the config table and `.env.example`.
      State plainly that **the portal and the sync API share one origin and one process**, and why (item 7's
      eviction).
    - `docker-compose.yml`: pass the three keys through, commented.
    - `scripts/smoke-test.ps1`: with `Web__Enabled=true`, fetch `/portal/login` and assert `200` and the CSP
      header. The full flows stay in the test suite; the smoke test proves the image serves pages and static
      assets.
    - `CLAUDE.md`: the architecture list gains the portal and the service layer; the auth paragraph gains "one
      user-level cookie scheme for the portal, validated against the same user row, never accepted by the JSON
      API"; the "Tenancy" section notes portal pages scope on the cookie's `user_id` exactly as JSON routes scope
      on the token's; the cross-repo section records the new contract rule from item 22 and the S11 handoff.
    - `plans/README.md`: the S11 row, the contract rule, and the note that the client no longer does account
      lifecycle.

27. **Docs that are now wrong and must be fixed here, not later.** `AccountEndpoints`' and `AdminEndpoints`'
    class docs say, respectively, that account management is how the desktop client manages an account and that
    there is no admin UI. S10's plan lists "An admin UI" as out of scope. Update the XML docs as part of
    group A/F, and leave S10's plan text alone apart from the forward pointer this plan's PR adds.

## Sequencing

- **A first, alone.** It is a pure refactor that the rest depends on, and landing it separately is what lets
  "no test modified" mean something.
- **B next.** Every page needs the scheme, the policies, the keys and the layout.
- **D before E.** The onboarding pages redeem links that only the operator pages mint. (The JSON routes can mint
  them too, so E *can* be tested without D, but building D first gives an end-to-end manual flow.)
- **C after B, anywhere relative to D and E.** It is independent, but it is last in priority because the
  desktop client still offers these operations until client plan 12 lands.
- **F last**, exactly as S9 and S10 did. Re-vendoring while the contract is still moving is the mistake S6–S8
  made once.
- **Client plan 12 is blocked on F's handoff**, and it should not ship before a server with the portal is
  available to its users, because it removes the client's only registration path.

## Out of scope

- **Reports in the portal.** The server's `/reports/*` rollups are the authoritative cross-device view and the
  client never consumes them. A portal "My time" page over `ReportService` is an obvious next plan (S12
  candidate). This plan builds the session it would need.
- **Time entry in the browser.** The portal manages accounts, not ledgers. Entering or editing time on the web
  would make the server a second client of its own data model, with its own merge story. Revisit only if it is
  wanted, and then as its own plan.
- **Self-service "forgot password", and email of any kind.** Still needs SMTP (S10 item 13).
- **Removing the compatibility routes, `/auth/claim` or the records they use.** Next wire-breaking release.
- **A separate portal origin or process.** Item 7 explains why one process. A split would need a distributed
  cache for revocation and is not worth it for a self-hosted box.
- **Accepting `X-Forwarded-Proto`/`X-Forwarded-Host`.** Item 6 explains why it is not needed.
- **2FA / passkeys, per-(email, IP) failure counting, per-user quotas, an audit log table, teams.** All still
  real, all still separate (see S10's Out of scope).
- **Any client change.** Client plan 12 is client-repo work.

## Acceptance criteria

- With `Web:Enabled` unset, every existing request behaves exactly as on S10, and the full existing suite passes
  with no test modified.
- An operator can enable the portal, complete `/portal/setup` on an empty database, issue an invite link, and
  have a second person register through it, sign in to the portal, and then sign in from the desktop client.
  No `curl`, no shared secret handed to the second person, and no desktop client needed to create the account.
- A regular user can, in the portal, see and remove their devices, change their password, sign out everywhere
  and delete their account. Each has the same effect on desktop installs as the corresponding S8 JSON route.
- An admin can, in the portal, do everything S10's `/admin/*` routes do, with the same guards, the same audit
  lines and the same immediate revocation.
- A password reset issued in the portal is redeemed in the portal. It dies on first use, signs out every device,
  clears the lockout, and is indistinguishable from an expired or unknown token when wrong.
- A portal cookie never authenticates a JSON route, and a bearer token never authenticates a portal page.
- No invite or reset token ever appears in a URL path or a log line, and loading a link spends nothing.
- A server restart or image upgrade does not sign portal users out.
- `WorkLedger.Contracts` contains only what the desktop client uses (plus the records the compatibility routes
  keep alive). `AuthCapabilitiesResponse` has one new trailing defaulted field. `Sync/`, `Payloads/` and
  `Reports/` are byte-identical to `1e343c8`, audited.
- `plans/S11-client-handoff.md` exists and is sufficient on its own for client plan 12's re-vendor.

## Suggested commit messages

One PR per group, off `main`:

```
refactor(auth): extract the account, admin and credential rules into services
feat(web): add the portal host, cookie scheme and persisted data-protection keys
feat(web): add operator pages for users, invites and password resets
feat(web): add setup, invite registration and password-reset pages
feat(web): add self-service account pages
docs(s11): move admin DTOs out of the contract, add PortalUrl, hand off to the client
```

## Prompt for Claude Code

> Read `CLAUDE.md`, `plans/S10-multi-user-operations.md` and this plan first. The desktop client is being
> reduced to time tracking plus sign-in; **every account operation moves to a web portal served by this
> process**, for regular users and admins alike.
>
> Start with group A as its own PR: extract the login verification, the account-deletion pipeline (currently
> written twice), the admin operations with their guards, eviction and audit line, and the invite, reset,
> registration and self-service logic into services, with their XML docs moving with them. **No behaviour
> changes, and no existing test may be modified.**
>
> Then build the portal as Razor Pages under `/portal`, behind `Web:Enabled` (default false) and a required
> `Web:PublicBaseUrl`. Add a **separate** cookie scheme; the default scheme stays JwtBearer. Portal pages use
> only portal policies and JSON routes use only bearer, so a cookie can never authenticate `/sync`, `/auth` or
> `/admin`. The cookie carries `user_id` and `stamp` and no admin claim; validate it on every request against a
> `user:{id}` snapshot of `(SecurityStamp, IsAdmin, IsDisabled)` and make `TokenRevocation.EvictUserAsync` evict
> that key too. Persist Data Protection keys to Postgres. Use `SecurePolicy.Always` and `Web:PublicBaseUrl` for
> links; **do not** widen `ForwardedHeaders`. Portal login goes through the extracted credential check, so it
> shares the per-user lockout and creates no `Device` row.
>
> Tokens travel only in URL fragments or POST bodies, never paths, and **no GET spends a token**. Build the
> operator pages, then `/portal/setup` (four bootstrap states, all secret-gated), `/portal/register` and
> `/portal/reset`, then the self-service pages. Add `PortalUrl` as a trailing defaulted field on
> `AuthCapabilitiesResponse`, move `AdminDtos.cs` and `ResetPasswordRequest` out of `WorkLedger.Contracts` into
> the server (the client never vendored them), and keep every JSON account route working as a documented
> compatibility route. Write the tests in item 25 at the **default** `Auth:StampCacheSeconds`, update the docs in
> items 26–27, and write `plans/S11-client-handoff.md`. **Do not modify the client repo.**
