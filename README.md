# WorkLedger Server

The backend for [WorkLedger](https://github.com/dante286/WorkLedger-Client): it holds the authoritative
copy of a user's projects, tasks and time entries, reconciles the changes each device pushes, and
computes the cross-device rollups no single device can. Self-hosted by design — one `docker compose up`
on a homelab box or a small VPS, with your data in a Postgres volume you own and back up.

It can also serve a small **web portal** from the same process, which is where accounts are created
and managed — the desktop client works offline on its own, and against a server it only signs in.

C# / ASP.NET Core Minimal APIs and Razor Pages on .NET 10, EF Core + Npgsql → PostgreSQL, JWT bearer
auth for the API, a cookie session for the portal, xUnit + Testcontainers.

- **Deploying it** → [Quickstart](#quickstart-self-hosted-with-docker-compose), then
  [Configuration](#configuration) and [Operations](#operations).
- **Working on it** → [Development](#development). The design record is `plans/` (S0–S11) and
  `plans/README.md`.
- **Running it for a team** → [The web portal](#the-web-portal), then
  [Running this for more than one person](#running-this-for-more-than-one-person).
- **Coming from a pre-user-accounts install** →
  [Upgrading from a pre-user-accounts deployment](#upgrading-from-a-pre-user-accounts-deployment).

## Architecture

Three projects (`WorkLedger-Server.slnx`), no controllers — everything is wired in `Program.cs`:

| Project | What it is |
|---|---|
| `src/WorkLedger.Server` | The API and the portal. Minimal API endpoints, EF Core + Npgsql, JWT bearer auth, `Sync/` (push/pull reconciliation), `Reports/` (read-only rollups), `Auth/` (the account rules, as services both surfaces call), and `Pages/` + `Web/` (the Razor Pages portal under `/portal`). |
| `src/WorkLedger.Contracts` | Dependency-free class library: the wire DTOs **the desktop client speaks, and nothing else**. No ASP.NET or EF references, because the client compiles these same files (see [Cross-repo](#cross-repo-the-contract-is-vendored)). The admin API's shapes are server-internal. |
| `tests/WorkLedger.Server.Tests` | xUnit. `WebApplicationFactory<Program>` plus `Testcontainers.PostgreSql`, so the data, auth, sync and report tests each run against a real throwaway Postgres. |

Three properties shape everything else:

- **Every endpoint is authenticated by default.** `Program.cs` installs an authorization
  `FallbackPolicy` requiring an authenticated user, so an endpoint is protected unless it explicitly
  opts out with `.AllowAnonymous()` — only `/`, `/health` and the five endpoints a caller with no token
  needs (`/auth/capabilities`, `/auth/register`, `/auth/claim`, `/auth/login`, `/auth/password/reset`)
  do. Adding an endpoint cannot accidentally leave it open. Portal pages each carry a portal policy or
  an explicit anonymous marker; a page with neither falls to that same bearer-only fallback and fails
  closed.
- **Scope always comes from the credential.** `ICurrentContext` reads `user_id`/`device_id` out of the
  JWT claims, and every sync and report query filters on that user; portal pages scope on the session
  cookie's `user_id` the same way. Nothing in a request body or URL can widen it. Ids in routes and
  payloads are cross-device `SyncId` Guids, never the server's internal int ids, so an id belonging to
  another user is simply a 404.
- **Two authentication schemes, never merged.** The JSON API accepts bearer tokens only and the portal
  accepts its cookie only. A cookie that could reach `/sync/*` or `/admin/*` would expose the JSON API
  to cross-site request forgery; a token that could reach a portal page would skip the portal's
  antiforgery and session rules.

A **user account is the top of the tenancy tree**: one `User` row owns many `Device` rows, and every
device syncs the same projects, tasks and time entries back under that user. **Credentials are
user-level; tokens are device-level** — a person signs in *from a device*, and what comes back is a JWT
scoped to that `(user, device)` pair. A browser signed in to the portal holds neither: it gets a
short, user-level session and creates no device. Registration is gated on the `Bootstrap__Secret`
signup secret **or** a single-use invite, and can be switched off entirely with
`REGISTRATIONS_ALLOWED=false`. A deployment with more than one person on it also gets an operator
surface — the portal's Administration pages, and `/admin/*` for scripts — gated on a flag read from the
database on every request rather than carried in a token.

Nothing is seeded. A fresh database has zero users; the first account created — through
`/portal/setup` or `POST /auth/register` — is the first user, and its administrator.

## Quickstart: self-hosted with Docker Compose

Requires Docker with Compose v2. From a clean checkout:

```bash
cp .env.example .env

# Fill in the three required secrets. On Linux/macOS:
#   Jwt__SigningKey    — at least 32 bytes; signs device tokens
#   Bootstrap__Secret  — the operator credential (/portal/setup; /auth/register, /auth/claim, /admin/bootstrap)
#   POSTGRES_PASSWORD  — the bundled database's password (set it before the first boot)
openssl rand -base64 48   # once per secret
$EDITOR .env

docker compose up -d
```

That pulls the published API image (`ghcr.io/dante286/workledger-api`, tagged `WORKLEDGER_VERSION`,
`latest` by default), starts Postgres, waits for it to pass its healthcheck, then starts the API —
which applies the EF migrations before it serves anything. To run this checkout instead of a published
release, `docker compose up -d --build` builds the image from source. A fresh host needs no other
provisioning step, and no account: the database comes up empty and the first registration creates its
first user.

```bash
curl http://localhost:8080/health      # {"status":"ok","utc":"..."}
curl http://localhost:8080/            # {"name":"WorkLedger.Server","version":"..."}
docker compose ps                      # both services should read "healthy"
```

If the API container exits instead, `docker compose logs api` names the configuration key it is missing
— it refuses to start on a missing signing key, bootstrap secret or database rather than serving
half-configured. Compose itself refuses the `up` if a required variable is absent from `.env`.

### The web portal

The server can serve a small set of plain HTML pages under `/portal`, and **that is where accounts are
made and managed**: first-run setup, signing up from an invite link, redeeming a password-reset link,
a person's own devices, password and account deletion, and an administrator's users and invites. The
desktop client works fully offline on its own and, pointed at a server, only signs in — it does no
account lifecycle. So any deployment with a second person on it, or anyone who would rather not drive
`curl`, wants the portal on.

It is **off by default, and off means absent**: no page, no cookie, no new route, so upgrading changes
nothing until you opt in. To turn it on:

```dotenv
# .env
Web__Enabled=true
Web__PublicBaseUrl=https://ledger.example.com    # the address browsers reach this server at
```

`Web__PublicBaseUrl` is **required** once the portal is on — the container refuses to start without
it, naming the key — and must be the `https://` address your TLS proxy serves (see
[Putting it behind TLS](#putting-it-behind-tls)); only a `localhost` address may be `http://`, because
the session cookie is `Secure` and a browser will not send it back over plain http anywhere else. It
is the **only** thing invite and reset links are built from, never the request's `Host` header, which
the caller controls. It is also what `GET /auth/capabilities` hands the desktop client as `portalUrl`,
so the app can send people to the right page instead of guessing at paths. `Web__SessionHours` (8 by
default, sliding) is how long a portal session lasts; there is deliberately no "remember me".

**The portal and the sync API share one origin and one process, and that is deliberate.** Every
revocation — a password change, signing out everywhere, a suspension, a demotion, a deletion — evicts
the in-memory revocation cache of the process that made it, which is what makes it bite on the very
next request rather than up to `Auth__StampCacheSeconds` later. A portal running as a separate service
would revoke without evicting the API's cache, and every revocation would quietly go back to lagging.
So there is nothing extra to deploy: same container, same port, same proxy route. A browser that asks
for `/` is sent to `/portal`; everything else asking for `/` still gets the JSON name and version.

Two consequences worth knowing:

- **A browser never holds a device token.** Signing in to the portal gives a short, user-level session
  cookie and creates no device, so it never appears in anyone's device list. The session is checked
  against the same user row as a token on every request, so a password change, signing out
  everywhere, a suspension or a deletion ends it at once too. Signing out of the portal signs out only
  that browser — never your desktop installs.
- **The two credentials never cross.** Every JSON route refuses the portal cookie and every portal page
  refuses a bearer token. The portal's forms all carry antiforgery tokens; the JSON API, which has
  none, is never reachable with a cookie, so the portal adds no cross-site-request-forgery exposure to
  it.

Its data-protection keys, which encrypt the session cookie and the antiforgery tokens, are kept in
Postgres (`DataProtectionKeys`) rather than the container's filesystem, so a restart or an image
upgrade does not sign anyone out. They sit unencrypted in that table, which is acceptable for the
same reason the table is where it is: anyone who can read it can already read every user row. Your
dumps carry them.

### First run

**With the portal on**, open the server in a browser and go to **`/portal/setup`**. On an empty
database it offers exactly one thing: **create the first account**, which is the administrator. It asks
for `Bootstrap__Secret` — every step on that page does, because the first person to find a fresh server
would otherwise own it — and signs you straight in. Then install the desktop app, give it the server
address the portal shows you, and sign in with the same email and password; each install enrols
itself as a device the first time it signs in.

`/portal/setup` knows the server's state and offers only the step that state allows: create the first
account on an empty database; **claim** the data of a pre-user-accounts install (see
[Upgrading from a pre-user-accounts deployment](#upgrading-from-a-pre-user-accounts-deployment)); or,
when there are users but no administrator, let one of them sign in and promote themselves. Once an
administrator exists the page is a `404`, so it does not advertise a secret prompt on a running
server.

#### Without the portal, or from a script

Two steps, and they are different kinds of thing. **Registration is a user-level act** gated by the
signup secret, done once. **Signing in is per device** and is the only source of a token. (These account
routes are *compatibility routes* since the portal arrived: they keep working exactly as they did, for
desktop builds older than client 0.3.0 and for your scripts, but new account features land in the
portal only.)

Ask the server what its rules are first — a client is expected to do this, and it costs nothing:

```bash
curl http://localhost:8080/auth/capabilities
# {"registrationEnabled":true,"registrationRequiresSecret":true,"minPasswordLength":12,
#  "hasUnclaimedAccount":false,"maxFailedLogins":5,"lockoutMinutes":15,
#  "registrationAcceptsInvite":true,"portalUrl":null}
```

`portalUrl` is `<Web__PublicBaseUrl>/portal` while the portal is on, and `null` while it is off.

Create the account. The secret goes in the `X-Bootstrap-Secret` header; nothing else on the API reads
it:

```bash
Bootstrap__Secret=$(grep '^Bootstrap__Secret=' .env | cut -d= -f2-)   # the value you put in .env

curl -X POST http://localhost:8080/auth/register \
  -H "X-Bootstrap-Secret: $Bootstrap__Secret" \
  -H 'Content-Type: application/json' \
  -d '{"email":"sam@example.com","password":"correct-horse-battery","displayName":"Sam"}'
# 201 {"id":1,"email":"sam@example.com","displayName":"Sam","createdAtUtc":"2026-..."}
```

No token comes back, deliberately: there is no device to bind one to yet. Now sign in from each device,
passing that device's own stable id:

```bash
# Answers {"token":"eyJ...","expiresAtUtc":"2027-...","user":{"id":1,"email":"sam@example.com",...}};
# jq keeps just the token for the calls below.
token=$(curl -s -X POST http://localhost:8080/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"sam@example.com","password":"correct-horse-battery","deviceId":"laptop-01","deviceLabel":"Work laptop"}' \
  | jq -r .token)

curl http://localhost:8080/auth/whoami -H "Authorization: Bearer $token"
# {"userId":1,"email":"sam@example.com","displayName":"Sam","deviceId":"laptop-01"}
```

`deviceId` is the client's own stable per-install identifier. **Enrolment is a side effect of logging
in**: the first login from a `deviceId` creates the device row, and a later login from the same id
refreshes its token, label and last-seen rather than creating a second device. The same `deviceId`
under a *different* user is a different device.

Every other endpoint wants that `Authorization: Bearer <token>` header and returns 401 without it.
Tokens last `Jwt__ExpiryDays` (365 by default) and there is no refresh; what replaces short expiry is
revocation, which is immediate — see
[Security posture](#security-posture-and-deferred-hardening).

Signing in is rate-limited per IP, and an account locks itself after repeated failures, so a wrong
password a handful of times in a row is answered `423` with a `Retry-After` rather than another `401`.
It unlocks itself; nothing needs doing.

### Running this for more than one person

Everything above still describes a one-person server, and nothing below changes it: a deployment that
never opens the portal's Administration pages or calls an `/admin/*` route behaves exactly as it did
before. What follows is for when a second person needs an account. It assumes
[the web portal](#the-web-portal) is on; every step also has a `curl` form, at the end.

**The first account on an empty database is an administrator.** No step, no flag — if you went through
`/portal/setup`, you already are one. A deployment that predates the operator surface has users but no
admins: open `/portal/setup`, which in that state offers **sign in and promote yourself**, gated on
`Bootstrap__Secret` as well as your password. It refuses once *any* admin exists, so it cannot be used
a second time to grant yourself somebody else's server.

**Onboard people with invite links, not the signup secret.** In the portal, **Administration →
Invites → Create invite link**. Optionally bind it to one address, shorten or lengthen its life
(`Auth__InviteExpiryDays` by default), and leave yourself a note. The page shows a link —
`https://ledger.example.com/portal/register#invite=…` — **once**: the server keeps only a SHA-256 of
the token, so it is never recoverable afterwards. Send it however you talk to them. It opens a sign-up
form with the invite already filled in; they choose their own password, land signed in to the portal,
and are shown the server address to paste into the desktop app.

- **Opening the link spends nothing.** Chat and mail apps fetch links to build previews; only submitting
  the form redeems the invite, and it works once. A sign-up that fails — the address is already taken,
  say — leaves the invite unspent.
- **The token rides after the `#`**, which a browser never sends to a server, so it cannot reach this
  server's request log, your proxy's access log or a `Referer`. If the link gets mangled on the way, the
  form has a field to paste the code into.
- An invite is single-use, expires, can be bound to one address, can be revoked from the same page
  until it is used, and leaves a record of who issued it. The secret is none of those things — handing
  it over makes someone a permanent signup credential.

Then **close the secret path**, which is the point of the exercise rather than an optional extra:

```dotenv
# .env  — an invite becomes the only way to create an account.
Auth__SignupSecretEnabled=false
```

`/auth/capabilities` advertises both switches. The secret keeps its other jobs — `/portal/setup`,
`POST /auth/claim` and `POST /admin/bootstrap` — and `REGISTRATIONS_ALLOWED=false` still closes
registration outright, invites included (the sign-up page then says registration is closed).

**Day-to-day operations** are **Administration → Users**: unlock someone who locked themselves out,
suspend an account without destroying it (and restore it), make someone a second administrator, issue a
password-reset link, or delete a leaver — typing their email to confirm which account. Every button runs
exactly the code the matching `/admin/*` route runs, with the same guards and the same audit line. Two
rules are worth knowing before you need them:

- **You cannot suspend or delete your own account from the Users page**, and you cannot leave the
  server with no administrator; the page says why when it refuses. Stepping down *is* allowed once
  somebody else can administer — that is how an operator hands over, and the page then sends you back
  to your own account, because the Administration pages will no longer let you in.
- **Admin status is read from the database on every request**, never from a token or the session
  cookie. A demotion, a suspension or a deletion takes effect on that person's very next request — in
  the portal and on every desktop install — not when some credential eventually expires.

**A forgotten password** is an admin action rather than a `psql` session. **Users → Password reset
link** shows a single-use link, `…/portal/reset#token=…`, once, with its expiry
(`Auth__PasswordResetExpiryHours`). Hand it over; they choose the new password on that page and you
never see it. Every one of their devices is signed out and signs in again, a lockout is cleared, and
the page does *not* sign them in — the link proves they hold the link; the next sign-in proves the
password. It dies on first use, and issuing a second one kills the first. An unknown, expired or
already-spent link all get the same message. **No email is sent** by any of this: the server has no
mail, so passing links on is yours to do, and there is still no self-service "forgot my password".

Everyone, administrator or not, manages their own account in the portal too: the devices signed in to
it (removing one signs that install out and deletes none of its data), their password, signing out
everywhere, and deleting the account and everything in it.

If nobody can administer the server at all, the `psql` recovery in
[Recovering a lost password](#recovering-a-lost-password) is still there.

#### From a script: the `/admin/*` routes

The portal is a front end over the same operations, not a replacement for them: `/admin/*` stays the
scripting interface, documented in the [operator table](#endpoints), and is not deprecated. The same
flow as above, by hand:

```bash
# Promote yourself on a deployment with users but no admin. Both credentials: the token says who
# you are, the secret says you are the operator. 204; 409 once any admin exists.
curl -X POST http://localhost:8080/admin/bootstrap \
  -H "Authorization: Bearer $token" \
  -H "X-Bootstrap-Secret: $Bootstrap__Secret"

# Create an invite. The token comes back HERE AND NOWHERE ELSE.
curl -X POST http://localhost:8080/admin/invites \
  -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
  -d '{"email":"alex@example.com","note":"new starter"}'
# 201 {"token":"y1Qk...","invite":{"id":1,"email":"ALEX@EXAMPLE.COM","expiresAtUtc":"2026-...",...}}

# They register with it in the body, and no header at all -- or you send them
# ${Web__PublicBaseUrl}/portal/register#invite=y1Qk... and they use the page.
curl -X POST http://localhost:8080/auth/register \
  -H 'Content-Type: application/json' \
  -d '{"email":"alex@example.com","password":"correct-horse-battery","inviteToken":"y1Qk..."}'
# 201 -- then POST /auth/login from each of their devices.

# Issue a password reset, shown once.
curl -X POST http://localhost:8080/admin/users/2/password-reset -H "Authorization: Bearer $token"
# 200 {"token":"Hn4v...","expiresAtUtc":"2026-..."}

# Them, with no token of their own and no old password (or ${Web__PublicBaseUrl}/portal/reset#token=Hn4v...):
curl -X POST http://localhost:8080/auth/password/reset \
  -H 'Content-Type: application/json' \
  -d '{"resetToken":"Hn4v...","newPassword":"a-new-correct-horse"}'
# 204 -- every one of their devices is signed out and signs in again.
```

## Endpoints

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /` | none | Name and running version (the image's `VERSION` build arg). With the portal on, a browser (`Accept: text/html`) is redirected to `/portal` instead. |
| `GET /health` | none | Liveness. Touches no database, which is why it is also the container's `HEALTHCHECK`. |
| `GET /auth/capabilities` | none | This server's account policy, so a client can render the truth instead of guessing: whether registration is open, which credentials it accepts (the secret, an invite, or both), the minimum password length, whether an unclaimed account is waiting, the lockout numbers, and `portalUrl` — where the web portal is, or `null` when it is off. Configuration, not secrets. |
| `POST /auth/register` | `X-Bootstrap-Secret` header **or** an `inviteToken` in the body | Create a user. `201` and the user; **no token** — tokens are device-scoped. `403` registration disabled, `401` neither credential accepted, `400` bad email or password, `409` email taken, and `409` for an invite that is expired, spent, or addressed to somebody else. |
| `POST /auth/claim` | `X-Bootstrap-Secret` header | Attach an email and password to a pre-user-accounts account, in place, keeping its data and its devices. `409` when there is nothing claimable. Deliberately **not** gated by `REGISTRATIONS_ALLOWED`. |
| `POST /auth/login` | none (credentials in the body) | **The only source of a token.** Enrols the request's `deviceId` under the authenticated user and mints a JWT for that `(user, device)`. `400` without a `deviceId`, `401` for any bad credential, `423` locked out, `429` rate-limited. |
| `GET /auth/whoami` | bearer | The user and device the caller's token resolves to. |
| `POST /auth/password` | bearer | Change the password. Re-verifies the current one (`401` if wrong), then bumps the security stamp — **killing every token, the caller's included** — and hands back a replacement. `400` for a new password below the minimum; `409` for the unclaimed pre-user-accounts row, which has no password to change. |
| `POST /auth/password/reset` | none (a reset token in the body) | Spend a token an admin issued out of band. `204` and **no token back**: this call names no device, so every device signs in again. `400` for a token that is unknown, expired or already spent — the same `400`, deliberately — and for a new password below the minimum. |
| `GET /auth/devices` | bearer | Every device signed in to this account, with `isCurrent` marking the caller's. |
| `DELETE /auth/devices/{deviceId}` | bearer | Revoke one device's access. `204`; `404` for an id this user does not own. Leaves that device's synced rows alone — they belong to the user, not the device. Removing your own is allowed and means "sign this device out". |
| `POST /auth/logout-all` | bearer | Bump the security stamp: every device signed out at once, with a fresh token returned for the caller. |
| `DELETE /auth/account` | bearer | Hard-delete the account and everything under it. Two independent gates: the current password (`401`) and a typed `confirmEmail` (`400`). `409` for the unclaimed row. Immediate and irreversible. |
| `POST /sync/push` | bearer | Submit a batch of create/update/delete changes; returns one result per change, in request order. `400` without a `changes` array. |
| `GET /sync/pull?since=<iso-8601>` | bearer | Changes from other devices with `UpdatedAt > since`. Omit `since` for everything; a `since` that is not an ISO-8601 timestamp is a `400`. |
| `GET /reports/weekly?weekOf=<yyyy-MM-dd>` | bearer | One ISO week (Monday–Sunday, UTC), across every device of the signed-in user. |
| `GET /reports/projects/{projectSyncId}?from=&to=` | bearer | One project over an optional inclusive UTC date range. |
| `GET /reports/tasks/{taskSyncId}?from=&to=` | bearer | One task, with a per-day breakdown. |

All three report endpoints also take `?format=csv` (see [Reports](#reports)).

**Compatibility routes.** `POST /auth/register`, `POST /auth/claim`, `POST /auth/password`,
`POST /auth/password/reset`, `GET /auth/devices`, `DELETE /auth/devices/{deviceId}`,
`POST /auth/logout-all` and `DELETE /auth/account` keep working exactly as described above, for desktop
builds older than client 0.3.0 and for scripts. (0.3.0 and later have no account UI and only sign in.)
Account management now lives in [the web portal](#the-web-portal), and **no new behaviour lands on
these routes**. They go at the next deliberately wire-breaking release, not before. `POST /auth/login`,
`GET /auth/capabilities` and `GET /auth/whoami` are what the desktop client speaks and are not on the
list.

**Operator routes.** Every one of these is `401` without a token, and every one but `/admin/bootstrap`
is `403` with a non-admin's, enforced by an authorization policy whose claim is resolved from the
database on each request rather than read out of the JWT — so a demotion bites on the very next
request. `/admin/bootstrap` cannot be behind that policy, since it exists for the state in which nobody
satisfies it; it wants the secret as well as a token instead. The routes are always mapped — on a
deployment with no admin they answer `403` to everyone — and nothing here is reachable with the
bootstrap secret alone.

| Endpoint | Purpose |
|---|---|
| `POST /admin/bootstrap` | Promote the calling user. Wants the bearer token **and** `X-Bootstrap-Secret`: the token says who, the secret says operator. `204`; `401` without both; `409` once any admin exists. |
| `GET /admin/users` | Every user, with device count, last login, and whether they are an admin, currently locked out, or suspended. |
| `POST /admin/users/{id}/unlock` | Clear a failed-login lockout. `204`, also when they were not locked; `404` for an unknown id. |
| `POST /admin/users/{id}/disable`, `POST /admin/users/{id}/enable` | Suspend or restore an account. A suspended user's existing tokens are refused on their next request, and their sign-in answers the same `401` as a wrong password — no new way to discover who has an account here. `204` (also if already in that state); `404` for an unknown id; `409` for suspending yourself or the last administrator. |
| `POST /admin/users/{id}/admin`, `DELETE /admin/users/{id}/admin` | Promote or demote. `204`; `404` for an unknown id; `409` when it would leave the server with no administrator. Demoting *yourself* is allowed once someone else can administer — that is the handover. |
| `POST /admin/users/{id}/password-reset` | Mint a single-use reset token and return it **once**. `200`; `404` for an unknown id; `409` for the unclaimed pre-user-accounts row, which has no password to reset. A second issue invalidates the first. |
| `DELETE /admin/users/{id}` | Hard-delete another user and everything under them, gated on a typed `confirmEmail` that must match **that** account (`400`). `404` for an unknown id; `409` on yourself — use `DELETE /auth/account`, which re-verifies your password — or on the last administrator. |
| `POST /admin/invites` | Create an invite. `201`, and the token appears **here and nowhere else**; the server stores only a SHA-256 of it. Optional `email` binds it to one address, `expiresInDays` overrides `Auth__InviteExpiryDays`, `note` is for your own benefit. `400` for a malformed `email` or an `expiresInDays` below 1. |
| `GET /admin/invites` | Every invite — metadata, never the token. Redeemed rows are kept: an invite is the record of how an account came to exist. |
| `DELETE /admin/invites/{id}` | Revoke an unredeemed invite. `204`; `404` unknown; `409` if it has already been redeemed, since deleting it would erase that record and it is unusable anyway. |

These are **not** compatibility routes: they are the operators' scripting interface, and the portal's
Administration pages call exactly the same code, with the same guards and audit line.

**Portal pages** (only while `Web__Enabled=true`; with it off none of them exists). Every form is a
`POST` carrying an antiforgery token, and a page never spends a token just by loading.

| Page | Who | What |
|---|---|---|
| `/portal/login`, `/portal/logout` | anyone | Sign in with email and password; the same lockout and rate limit as `/auth/login`, and no device is created. Sign-out is a `POST` and ends only that browser's session. |
| `/portal/setup` | anyone holding `Bootstrap__Secret` | First run: create the first account, claim a pre-user-accounts install, or promote yourself when nobody is an admin. `404` once an admin exists. |
| `/portal/register` | anyone holding an invite link (or the secret, while `Auth__SignupSecretEnabled`) | Create an account and sign straight in. |
| `/portal/reset` | anyone holding a reset link | Choose a new password; every device is signed out, and nobody is signed in. |
| `/portal/denied`, `/portal/deleted` | anyone | Informational: where a non-admin lands on an Administration page, and where a just-deleted account lands once its session has gone with it. |
| `/portal`, `/portal/account/devices`, `/portal/account/security`, `/portal/account/delete` | signed in | Your account, your devices, password change and sign-out-everywhere, and account deletion. |
| `/portal/admin`, `/portal/admin/users`, `/portal/admin/invites` | signed in **and** an admin | An index of the two, then everything the operator routes above do. |

## Configuration

Everything is environment-driven; `docker-compose.yml` reads it from `.env` (start from `.env.example`,
which is the annotated list). Names use ASP.NET Core's `__` section separator, so `Jwt__SigningKey` is
the `Jwt:SigningKey` key.

**Required** — the app fails fast at startup without them:

| Variable | Notes |
|---|---|
| `Jwt__SigningKey` | Signs device tokens. At least 32 bytes. Rotating it invalidates every issued token, so every device signs in again. Not stored in the database — back it up with your dumps. |
| `Bootstrap__Secret` | The operator credential: `/portal/setup`, and `POST /auth/register`, `POST /auth/claim` and `POST /admin/bootstrap` via the `X-Bootstrap-Secret` header. It cannot mint a token or a session; only a password can. Treat it like a password. |
| `POSTGRES_PASSWORD` | Password for the bundled Postgres. Baked into the volume by `initdb` on the first boot — changing it later needs an `ALTER USER`, not an edit here. |

A database must be reachable one of two ways: the four `POSTGRES_HOST` / `POSTGRES_DB` /
`POSTGRES_USER` / `POSTGRES_PASSWORD` variables, plus an optional `POSTGRES_PORT` (`5432` when unset)
— what compose passes, so the password lives in exactly one place — or a full
`ConnectionStrings__Postgres` DSN, which wins when set and is how you point at an external or managed
Postgres. A blank value counts as unset. The resolution and its failure message live in
`src/WorkLedger.Server/Data/PostgresConnection.cs`.

**Optional.** `docker-compose.yml` passes each of the server's optional keys through **blank** when
`.env` does not set it, and the server treats a blank value as unset — so leaving a key out and setting
it empty both mean the default below. (`POSTGRES_DB`/`POSTGRES_USER`, `API_PORT`/`API_BIND` and
`WORKLEDGER_VERSION` are compose's own, with the defaults written into the compose file;
`ASPNETCORE_ENVIRONMENT` is set in the image and compose does not pass it at all.)

| Variable | Default | Notes |
|---|---|---|
| `POSTGRES_DB` / `POSTGRES_USER` | `workledger` | Database and role for the bundled Postgres. |
| `Jwt__ExpiryDays` | `365` | Device token lifetime. Long on purpose: there is no refresh, and revocation is the security stamp rather than expiry. |
| `Jwt__Issuer` / `Jwt__Audience` | `WorkLedger.Server` / `WorkLedger.Client` | Validated on every request; change both sides together. |
| `REGISTRATIONS_ALLOWED` | `true` | Whether `POST /auth/register` accepts signups at all (`403` when off). `Auth__RegistrationEnabled` is the canonical key and wins if both are set. `POST /auth/claim` ignores this flag by design. |
| `Auth__SignupSecretEnabled` | `true` | Whether `X-Bootstrap-Secret` still satisfies `POST /auth/register`. Set `false` and an **invite** becomes the only way to create an account — which is the point of invites, not an extra. Defaults on so an upgrade changes nothing. `/auth/claim` and `POST /admin/bootstrap` are unaffected. Reported by `/auth/capabilities`. `SIGNUP_SECRET_ENABLED` is the flat alias; the `Auth__` key wins if both are set. |
| `Auth__InviteExpiryDays` | `14` | How long an invite lasts when the issuing admin does not choose. `expiresInDays` on the request overrides it per invite. **There is no value meaning "never"** — an invite that cannot expire is a standing credential, which is the thing invites exist to stop being. |
| `Auth__PasswordResetExpiryHours` | `24` | How long an admin-issued reset token lasts. Hours where an invite gets days, and on purpose: an invite is arranged in advance, a reset is handed over during a conversation happening now. |
| `Auth__MinPasswordLength` | `12` | Shortest password `/auth/register`, `/auth/claim`, `/auth/password` and `/auth/password/reset` accept. Reported by `/auth/capabilities`. |
| `Auth__MaxFailedLogins` | `5` | Consecutive failed sign-ins before an account locks. `0` disables the lockout — it does not mean "lock on the first failure" — and does so from the next request, even for an account inside a window stamped while it was armed. |
| `Auth__LockoutMinutes` | `15` | How long that lockout lasts. The failure counter resets once the window expires, so an account cannot end up locked permanently. |
| `Auth__LoginRateLimitPerMinute` | `10` | Per-IP limit on `/auth/register`, `/auth/claim`, `/auth/login` and `/auth/password/reset`, and on the portal's credential forms — sign-in, setup, register, reset, password change and account deletion — which share the same bucket (a `GET` that only renders a form spends nothing); over it is `429` with `Retry-After`. `0` disables it. Which address it partitions on depends on `Network__TrustedProxies` below. |
| `Network__TrustedProxies` | *(unset)* | Comma-separated IP addresses and CIDR blocks of the reverse proxies whose `X-Forwarded-For` this deployment believes. **Unset means no forwarded-header processing at all** — the middleware is not even registered — so the rate limiter partitions on the socket address, as it always has. Set it and each caller behind the proxy gets their own bucket. A malformed entry fails startup naming the value; see [Putting it behind TLS](#putting-it-behind-tls). |
| `Auth__StampCacheSeconds` | `30` | How long a token's revocation check is cached. **This is the revocation lag**, and only for revocations that happen elsewhere: the endpoints that revoke evict the entry themselves, so the common case is immediate. `0` checks the database on every authenticated request. |
| `Web__Enabled` | `false` | Serve [the web portal](#the-web-portal) under `/portal`. Off means absent: no page, cookie or route, and every existing request behaves as before. |
| `Web__PublicBaseUrl` | *(unset)* | **Required when `Web__Enabled=true`** — startup fails naming the key without it. The absolute `https://` address browsers reach this server at (only `localhost` may be `http://`), no path, query or fragment. The only source of invite links, reset links and `portalUrl`; never the request's `Host`. |
| `Web__SessionHours` | `8` | A portal session's sliding lifetime. There is no "remember me". |
| `Database__MigrateOnStartup` | `true` | Set `false` to apply migrations as a separate step instead (see [Schema changes](#schema-changes)). |
| `API_PORT` / `API_BIND` | `8080` / `0.0.0.0` | Host side of the published port. `API_BIND=127.0.0.1` when a reverse proxy is the only thing that should reach it. |
| `WORKLEDGER_VERSION` | `latest` | Image tag to run. It is also the version a local `docker compose up --build` stamps into `GET /`, which reads `0.0.0-dev` when it is unset. |
| `ASPNETCORE_ENVIRONMENT` | `Production` (set in the image) | `Development` turns on the developer exception page — do not use it on a deployed instance. |

## Sync

`POST /sync/push` and `GET /sync/pull` implement last-write-wins reconciliation over the shared
`WorkLedger.Contracts` DTOs. The rules are stated once, in full, in the XML doc comment on
`SyncService` (rules A–F) and mirrored by the client and by `plans/S3-sync-endpoints.md`; read that
comment before changing merge behaviour. In outline:

- Higher `Version` wins. A losing push is answered with the server's own version and payload, so the
  pusher can adopt the winner without waiting for its next pull.
- Deletes are accepted at `>=` and stored one version higher, so a tombstone strictly beats every
  peer's copy (a deliberate deviation from strict LWW). The client once needed the `>=` because its
  deletes did not bump the version; WL-93 fixed that, and the rule stays for client builds older than
  it — reverting it is a release decision, not a cleanup.
- Replays — a change at or below the server's version from the device that authored the live row
  (strictly below, for a delete), or a delete at or below an existing tombstone's version — are
  accepted as no-ops with no database write at all: `UpdatedAt` is the pull cursor, so an incidental
  write would re-broadcast the row to every other device.
- Results are **positional** — one per submitted change, in request order — because one batch can carry
  create → update → delete for the same entity. A batch is applied parents-first (project → task → time
  entry) inside one transaction, but answered in the order it arrived.
- A pull never returns the calling device its own writes.
- Push and pull are serialized **per user** on a Postgres advisory lock, so a pull may briefly wait for
  the same user's in-flight push to commit; pulls never wait on each other, and other users never wait
  at all. That is what stops a pull handing out a cursor past rows a concurrent push had not yet
  committed, which would otherwise never be pulled incrementally.

## Reports

Three read-only, user-scoped endpoints. They are the **authoritative cross-device rollup**: once
several devices sync into one user account, only the server holds the merged picture. (The client's Reports
tab computes its own local view and does not consume these.)

```
GET /reports/weekly?weekOf=<yyyy-MM-dd>
GET /reports/projects/{projectSyncId}?from=<yyyy-MM-dd>&to=<yyyy-MM-dd>
GET /reports/tasks/{taskSyncId}?from=<yyyy-MM-dd>&to=<yyyy-MM-dd>
```

**Parameters.** `weekOf` names a week, not a start date: any day in the ISO week resolves to that
Monday–Sunday, and omitting it means the current UTC week. `from`/`to` are **inclusive whole UTC days**,
so `from=2026-03-02&to=2026-03-02` is that entire day; either may be omitted, and the default is
all-time. `from > to` is a 400, not an empty report. Everything is UTC; the caller localizes. An unknown
or deleted project/task is a 404 — which is also the answer for one belonging to another user.

**What counts.** A time entry contributes only if it, its task, and that task's project are all alive
(tombstoned rows are excluded along the whole chain) and it has an `EndTime`. Its length is the stored
`DurationSeconds`, falling back to `EndTime − StartTime`. It lands wholly in the UTC day it *started*, so
an entry spanning midnight is not split — the same rule the client's own rollup uses. Entries still
running have no fixed length and are excluded from every total, but each report carries a
`runningEntries` count, so a caller can say "plus one timer still going" instead of silently
under-reporting. The counting rules are stated in full on `ReportService` (rules 1–6), with
`ReportQuery` as their SQL form.

The weekly report's `days` array always has seven entries, zero-filled, so it can be charted directly;
its overall total, per-project, per-task and per-day views are all folded from one aggregation and
cannot disagree.

**CSV export.** Add `?format=csv` (or send `Accept: text/csv` — the query parameter wins, and a wildcard
`*/*` stays JSON) to get an RFC 4180 download instead. An unrecognized `format` is a 400.

| Endpoint | Columns |
|---|---|
| `/reports/weekly` | `date,project,task,seconds` |
| `/reports/projects/{id}` | `project,task,seconds` |
| `/reports/tasks/{id}` | `date,project,task,seconds` |

The weekly file is the (day × project × task) grid the JSON's three breakdowns are each folded from, so
every one of them is a single pivot away in a spreadsheet. Days with no time have no row, and
`runningEntries` is JSON-only — a header-level scalar has no honest column in a table of recorded time.

Response DTOs live in `src/WorkLedger.Contracts/Reports/ReportDtos.cs`.

## Operations

### Backups

The named volume `workledger_workledger-postgres-data` **is** the deployment. Back up its contents, not
the container:

```bash
# Logical dump (portable across Postgres versions; restore with pg_restore)
docker compose exec -T postgres pg_dump -U workledger -Fc workledger > workledger-$(date +%F).dump

# Restore into an empty database
docker compose exec -T postgres pg_restore -U workledger -d workledger --clean --if-exists < workledger-2026-08-19.dump
```

A dump is a plain file: keep it somewhere off the host, and check it restores at least once. Two things
worth knowing before an emergency:

- Devices hold no unsynced authority once they have pushed, but they *do* hold local data the server has
  never seen, so a restore of an older dump plus a client push converges rather than losing that work —
  the higher `Version` still wins.
- The JWT signing key is **not** in the database. Losing `.env` means a new key, which kills every
  token and makes every device sign in again — recoverable, but only if someone still knows a
  password. Back it up (encrypted) alongside the dumps. Losing the `.env` *and* the passwords means
  the data survives in the volume with nothing able to authenticate against it; see
  [Recovering a lost password](#recovering-a-lost-password).

### Upgrading

```bash
# A published release: set WORKLEDGER_VERSION in .env (or stay on latest), then
docker compose pull                       # plain `up` reuses an image it already has
docker compose up -d                      # recreates the api container

# Or from source: `up` does not rebuild without --build
git pull
docker compose up -d --build
```

Migrations are applied by the API on boot, so an upgrade that adds a column needs no separate step. Take
a dump first anyway: a migration is the one kind of change that a rollback of the image cannot undo.

**Upgrading onto a release with the operator surface**, from one that already has users: nothing breaks
and nothing changes on its own — the new columns default to "not an admin, not suspended", so the
deployment carries on exactly as before. To use any of it, promote yourself once with
`POST /admin/bootstrap`; the step and the reasoning are in
[Running this for more than one person](#running-this-for-more-than-one-person). A deployment created
after this release needs nothing: its first account is already an admin.

**Upgrading onto a release with the web portal** changes nothing until you set `Web__Enabled=true`.
The migration adds one table, `DataProtectionKeys`, which stays empty while the portal is off. Turning
the portal on is the [two settings in The web portal](#the-web-portal) and a restart; existing desktop
installs keep their tokens and carry on syncing. The JSON account routes a pre-0.3.0 client may still
use are unchanged.

### Upgrading from a pre-user-accounts deployment

Releases before this one had **one account**, seeded on first boot, and any device holding the bootstrap
secret could mint itself a token. Now a **user account owns its devices**, and a token comes only from a
password. Your data carries over untouched — the migration renames tables in place, it never copies or
re-creates rows — but the tokens do not, and one HTTP call transfers ownership of the existing account.

**Do it in this order.** `POST /auth/claim` only works while the database holds *exactly one* user row
with no password. Register anybody else first and it answers `409` with nothing left to claim, and the
old account becomes unreachable — its data still there, nothing able to sign in to it.

```bash
# 1. Back up. This is a schema migration; the image rollback cannot undo it.
docker compose exec -T postgres pg_dump -U workledger -Fc workledger > pre-user-accounts.dump

# 2. Upgrade. The API migrates on boot: Accounts -> Users, plus the new credential columns.
docker compose pull
docker compose up -d
docker compose logs api | tail -20        # migration output, then the request log

# 3. Confirm there is something to claim.
curl http://localhost:8080/auth/capabilities
# ..."hasUnclaimedAccount":true...

# 4. Take ownership of it: your email, a password of at least Auth__MinPasswordLength.
Bootstrap__Secret=$(grep '^Bootstrap__Secret=' .env | cut -d= -f2-)
curl -X POST http://localhost:8080/auth/claim \
  -H "X-Bootstrap-Secret: $Bootstrap__Secret" \
  -H 'Content-Type: application/json' \
  -d '{"email":"sam@example.com","password":"correct-horse-battery"}'
# 200 {"id":1,"email":"sam@example.com","displayName":"Default","createdAtUtc":"2026-..."}
```

Then **sign every device in again** (`POST /auth/login`, as in
[First run](#without-the-portal-or-from-a-script)). With the portal on, `/portal/setup` does the claim
step for you: it offers **claim this server** while that one credential-less row exists, and asks for
the same secret. Every pre-upgrade token is
dead: they carry no security stamp and are refused on that basis alone, so there is no window in which
an old token still works. The device rows survive the upgrade, so a device signing in with the same
`deviceId` picks up its own row rather than creating a second one, and nothing re-syncs from scratch.

Two details worth knowing:

- **`Account__Name` is gone.** It named the seeded account and there is no seeder any more. If it is
  still in your `.env` it is simply ignored — nothing reads it, and leaving it there is harmless. The
  value it set is not lost: the migration renames that column to `DisplayName`, so it comes back as the
  claimed account's display name (`"Default"` above, if you never changed it).
- **Nothing forces you to close signup**, but you probably want to. Once your account exists, set
  `REGISTRATIONS_ALLOWED=false` in `.env` and `docker compose up -d`: `POST /auth/register` then answers
  `403`, `/auth/capabilities` advertises it so a client or script can tell before it tries, and the
  bootstrap secret stops mattering for signup. `POST /auth/claim` deliberately keeps working — an operator must
  always be able to claim their own data.

### Recovering a lost password

**Try an administrator first.** The portal's **Users → Password reset link** (or
`POST /admin/users/{id}/password-reset`) issues a single-use link you hand over, and covers every case except the one where nobody can administer the server — see
[Running this for more than one person](#running-this-for-more-than-one-person). What follows is the
fallback for when that is not available: no admin exists, or the only one is the account that is locked
out.

There is still no *self-service* reset, because the server sends no mail and so has no reset link to
send (see [Security posture](#security-posture-and-deferred-hardening)). What there is instead is the
database, which you own. Both recoveries below are `psql` against the volume, and both assume you can
reach it:

```bash
docker compose exec -it postgres psql -U workledger -d workledger
```

**If yours is the only account**, hand it back to `POST /auth/claim` by clearing its password. A row
with a null `PasswordHash` is the "unclaimed" state the upgrade path uses, and it can never
authenticate, so the row is inert until you claim it:

```sql
UPDATE "Users" SET "PasswordHash" = NULL, "Email" = NULL, "NormalizedEmail" = NULL,
                   "FailedLoginCount" = 0, "LockoutEndUtc" = NULL;
```

Then `POST /auth/claim` with the email and password you want, exactly as in the upgrade section above.
Projects, tasks, time entries and device rows are untouched.

**If there is more than one account**, that path is closed on purpose — "claim" cannot know which
account is yours once the deployment is multi-user. Copy a hash you do know the password for onto the
locked-out row instead, sign in, and change it in the portal (**Password & sign-out**) or with
`POST /auth/password`:

```sql
UPDATE "Users" SET "PasswordHash" = (SELECT "PasswordHash" FROM "Users" WHERE "NormalizedEmail" = 'KNOWN@EXAMPLE.COM'),
                   "FailedLoginCount" = 0, "LockoutEndUtc" = NULL
 WHERE "NormalizedEmail" = 'LOCKED@EXAMPLE.COM';
```

Neither statement bumps `SecurityStamp`, so tokens already issued to that account keep working — which
is usually what you want during a recovery. To sign every one of its devices out as well:

```sql
UPDATE "Users" SET "SecurityStamp" = gen_random_uuid()::text WHERE "NormalizedEmail" = 'LOCKED@EXAMPLE.COM';
```

That one takes up to `Auth__StampCacheSeconds` to bite, because a hand-written `UPDATE` cannot evict the
API's revocation cache the way `/auth/logout-all` does.

**If the problem is that nobody is an administrator**, you do not need `psql` for it — `/portal/setup`
lets any user sign in and promote themselves with the secret, or sign in from a device and
`POST /admin/bootstrap` with it, as in
[Running this for more than one person](#running-this-for-more-than-one-person). The `psql` equivalent
is `UPDATE "Users" SET "IsAdmin" = true WHERE "NormalizedEmail" = 'YOU@EXAMPLE.COM';`, which takes up
to `Auth__StampCacheSeconds` to be seen for the same reason as above.

### Putting it behind TLS

The API speaks plain HTTP inside the compose network and publishes one port; it does not terminate TLS.
For anything reachable from outside a trusted LAN, put a reverse proxy in front and let it hold the
certificate. Bind the API to localhost first, so the proxy is the only route in:

```dotenv
# .env
API_BIND=127.0.0.1
```

Then point NGINX (or Caddy, or Traefik) at `127.0.0.1:8080`, forwarding `Host`, `X-Forwarded-For` and
`X-Forwarded-Proto`. Nothing in the app depends on the request scheme — the API's auth is a bearer
header and there is no redirect-to-HTTPS — so the proxy needs no cooperation from the app to serve
traffic correctly.

**The portal needs nothing extra from the proxy either**: one route for the whole origin serves the
sync API and the portal alike. Its cookies are always issued `Secure`, whatever the proxy-to-app hop
is, because a browser accepts a `Secure` cookie whenever *its* connection is HTTPS — so the app does
not trust `X-Forwarded-Proto` to learn the scheme, and does not need to. What it does need is
`Web__PublicBaseUrl` set to the `https://` address the proxy serves, since that, not the forwarded
`Host`, is what every link it hands out is built from. A portal behind plain http on anything other
than `localhost` cannot work: the browser would never send the session cookie back. If you want the
portal reachable from fewer places than the API, restrict `/portal` at the proxy; the prefix is there
so you can.

**One thing does change behind a proxy, and there is a setting for it.**
`Auth__LoginRateLimitPerMinute` partitions on the socket's remote address, which behind a proxy is the
proxy for every request — so the whole deployment shares one bucket, ten sign-in attempts a minute
across all users rather than each. Name the hop and each caller behind it gets their own:

```dotenv
# .env  — the proxy's address as the API sees it, not its public one.
Network__TrustedProxies=172.18.0.5
# Several are fine, and CIDR blocks work: 10.0.0.0/24, 2001:db8::/32
```

**Only list hops you control.** `X-Forwarded-For` is caller-supplied, so trusting it from an untrusted
hop is strictly worse than one shared bucket: anyone could invent a fresh address per request and hand
themselves an unlimited one, turning the limiter off for exactly the attacker it exists to stop. The
app never blanket-trusts the header — it compares the connecting address against this list, reads only
`X-Forwarded-For`, and honours a single hop. There is no wildcard, and a value it cannot parse stops
the container at startup naming the entry, because a silently skipped typo looks identical to having
configured nothing.

It says which mode it is in on every boot, so `docker compose logs api | head` settles the question:

```
Trusting X-Forwarded-For from 172.18.0.5; rate limits partition on the forwarded client address.
Not trusting X-Forwarded-For (Network:TrustedProxies is unset); rate limits partition on the socket
address, which behind a reverse proxy is one bucket for the whole deployment.
```

Rate-limiting at the proxy instead is still a perfectly good answer — the real client address is known
there — in which case raise or disable the app's limit (`Auth__LoginRateLimitPerMinute=0`) and leave
`Network__TrustedProxies` unset. Either way the per-account lockout is unaffected: it keys on the
account, not the address. If the proxy runs as its own compose service, drop the `ports:` mapping from
`api` altogether and let the proxy reach it by service name on the shared network — its address on that
network is what goes in the list.

### Logs

`docker compose logs -f api`. One line per request — method, path, status — plus EF Core's migration
output on boot. Deliberately narrow: `Request.Path` excludes the query string and no header is logged,
which is what keeps bearer tokens and the bootstrap secret out of the log, since every secret the API
accepts arrives in a header. The portal keeps to the same rule by construction: passwords, the secret
and invite or reset tokens arrive in a `POST` body, and invite and reset links carry their token after
the `#`, which a browser never sends — so no token is ever in a path. A test captures every log line,
at every level, across issuing and redeeming both kinds of link, and fails if a token appears. EF's SQL logging is set to `Warning` in `appsettings.json` so a production
log is not a transcript of every query.

A few lines are normal and not worth chasing:

- On the **very first** boot, a `fail: Microsoft.EntityFrameworkCore.Database.Command` for
  `SELECT ... FROM "__EFMigrationsHistory"` — EF looking for a migrations table that does not exist yet,
  immediately before it creates it.
- With the portal **off**, `Microsoft.AspNetCore.DataProtection` warnings about keys not being
  persisted outside the container. Nothing uses data protection while the portal is off — the JWTs are
  signed with `Jwt__SigningKey`, which is yours and stable — so an ephemeral key ring is harmless.
- With the portal **on**, one `No XML encryptor configured. Key {…} may be persisted to storage in
  unencrypted form` each time a new key is created (the first boot, and roughly every 90 days after).
  That is the deliberate choice described in [The web portal](#the-web-portal): the keys live in
  Postgres, unencrypted, beside the user rows they protect sessions for.

### Smoke test

`scripts/smoke-test.ps1` drives a **running deployment** the way one person with two devices would:
read `/auth/capabilities`, register a throwaway account, sign in from two devices, push a project →
task → two time entries from one, pull them on the other, edit from the second and pull the edit back
on the first, push a stale edit and check the server hands back the winner, check every report agrees
with what was pushed (including the CSV header and the 404/400 paths), list the account's devices, and
delete the account again.

If `/auth/capabilities` advertises a `portalUrl`, it also fetches `/portal/login` and asserts a `200`
with the portal's `Content-Security-Policy`, that the stylesheet is served anonymously, and that JSON
responses carry none of the portal's headers; with the portal off it asserts `/portal` is not a page.
That proves the image serves pages and their static assets — the portal's flows are the test suite's.

```powershell
pwsh ./scripts/smoke-test.ps1              # against a stack that is already up
pwsh ./scripts/smoke-test.ps1 -Up          # docker compose up --build first
pwsh ./scripts/smoke-test.ps1 -KeepAccount # leave the throwaway account behind to poke at
pwsh ./scripts/smoke-test.ps1 -BaseUrl https://workledger.example.com -BootstrapSecret <secret>
```

It reads `API_PORT` and `Bootstrap__Secret` from `.env` when not given, asserts every step, and exits
non-zero on the first failure. This is not a substitute for `dotnet test` — the merge rules, the
counting rules and the auth rules are covered there. What only the smoke test catches is the
deployment: the image, the container configuration, migrations-on-boot, and a token minted by the
container being accepted by it. A few of its assertions can only be made against a real container, and
are there for that reason: registering with a wrong bootstrap secret is a `401`, signing in with a
wrong password is a `401`, and after `DELETE /auth/account` **both** devices' tokens are refused
immediately rather than after `Auth__StampCacheSeconds` — that last one fails if the endpoint's cache
eviction is ever dropped.

**It also does the multi-user leg, but only if it is allowed to administer the target.** On a fresh
volume the throwaway account is the first user and therefore an admin; on a deployment that already
has one, the script is a stranger and skips the leg rather than promoting itself into somebody else's
server. When it runs, it issues an invite, registers a second account with that token and **no shared
secret**, and asserts the two accounts cannot see each other — including the direction that matters
most, that an **admin can act on accounts and still cannot read their rows**.

**It cleans up after itself**, which the pre-user-accounts version could not: everything it writes
belongs to throwaway accounts (`smoke+<timestamp>@example.invalid`, and the invited
`smoke2+...`), and the final steps delete both — the invited one through `DELETE /admin/users/{id}`,
its own through `DELETE /auth/account`. It only ever deletes accounts it created. Things to know
before pointing it at a deployment you care about:

- **Registration has to be open** (`REGISTRATIONS_ALLOWED`), because the run starts by creating that
  account. Against a deployment with signup closed it stops at that step and says so.
- It spends POSTs on the per-IP credential rate limit (`Auth__LoginRateLimitPerMinute`, 10 by
  default): nine on a fresh volume, where the multi-user leg runs, and six where it is skipped — one
  fewer of each with `-KeepAccount`. One run fits the default with a request to spare; two inside the
  same minute can trip a `429`.
- **One row is left in `Invites` per run**, and that is the feature rather than a leak: both user
  references are `ON DELETE SET NULL` so the invite outlives the accounts, and a redeemed invite
  cannot be revoked. Each run leaves one redeemed row with two null user ids.
- If the leg ran, the throwaway account was **promoted to admin** along the way — and then deleted, so
  the deployment ends with exactly the administrators it started with, which for a fresh volume is
  none.

With `-KeepAccount` it skips the deletion and prints the throwaway credentials, so you can sign in and
look around; that account and its rows then stay until you delete them or drop the volume.
## Security posture and deferred hardening

### What it does

- **Nothing is anonymous** except `/`, `/health`, the five endpoints a caller with no token needs
  (`/auth/capabilities`, `/auth/register`, `/auth/claim`, `/auth/login`, `/auth/password/reset`), and,
  with the portal on, its sign-in, setup, register and reset pages, a few informational pages and its
  static assets — enforced by an authorization fallback policy rather than per-endpoint attributes, so
  a new endpoint cannot be left open by omission.
- **A browser never holds a device token, and the two credentials never cross.** The portal's session
  is a user-level cookie — `__Host-` prefixed, `HttpOnly`, `Secure`, `SameSite=Lax`, sliding over
  `Web__SessionHours` — carrying the user id and security stamp and **no admin claim**. It is
  validated against the same user row as a token on every request, so a stamp bump, a suspension, a
  demotion or a deletion ends it at once. The JSON API refuses the cookie, the portal refuses bearer
  tokens, and every portal form carries an antiforgery token.
- **Portal responses are hardened, and only portal responses.** A `Content-Security-Policy` with no
  `unsafe-inline` (the pages use no inline script or style), `Referrer-Policy: no-referrer`,
  `X-Content-Type-Options: nosniff` and `Cache-Control: no-store`. The portal's credential forms —
  sign-in, setup, register, reset, password change and account deletion — share the per-IP credential
  rate limit with `/auth/login`, and a portal sign-in spends the same per-account lockout budget as a
  device's.
- **Links are never built from `Host`.** Invite and reset links come from `Web__PublicBaseUrl` alone,
  carry their token in the fragment, and are spent only by a form post — never by opening them.
- **Every query is scoped to the token's user**, and a route id belonging to another user is a 404
  rather than a 403, so an id cannot be probed for existence.
- **Passwords are hashed with PBKDF2-SHA512**, salted per password and version-prefixed
  (`Auth/PasswordService.cs`, wrapping `PasswordHasher<T>` from `Microsoft.Extensions.Identity.Core`).
  The version prefix means an iteration-count bump years from now is a detectable format change rather
  than a silent lockout: a hash that is valid but outdated is replaced on the next successful login.
  Passwords are never logged and never returned.
- **Sign-in failures are uniform.** A wrong password, an unknown email, a credential-less account and
  a suspended one all answer the same `401`, so login cannot be used to find out who has an account
  here. (Registration is frank about a duplicate email, because getting that far already required the
  signup secret or an invite. The asymmetry is deliberate, and the XML docs on `CredentialCheck` and
  `RegistrationService` say why.)
- **Two independent brakes on guessing.** `Auth__MaxFailedLogins` consecutive failures lock one account
  for `Auth__LockoutMinutes` (`423` with `Retry-After`), and `Auth__LoginRateLimitPerMinute` caps the
  credential endpoints per IP (`429` with `Retry-After`). The lockout defends one account; the rate
  limit defends the box against someone spraying many addresses. The lockout's counter resets once its
  window expires, so an account can never end up permanently locked.
- **Tokens can be killed.** Every token carries a `stamp` claim, and every authenticated request
  re-checks the token's device row joined to its user — so a token stops working the moment its device
  is removed, its user is deleted or suspended (`User.DisabledAtUtc`), or the stamp changes (a password
  change, a redeemed password reset, or `POST /auth/logout-all`).
  That check is cached for `Auth__StampCacheSeconds` (30 by default), and the endpoints that revoke
  evict the entry themselves, so the TTL is only the lag for a revocation that happened elsewhere.
- **Re-authentication for the destructive things.** Changing a password and deleting an account both
  re-verify the current password even though the caller already holds a valid token: a stolen token must
  not be enough to take over or destroy an account. Deletion additionally requires the account's own
  email typed back.
- **Onboarding without sharing a credential.** An invite is single-use, expires, is revocable, can be
  bound to one address, and records who issued it and who spent it. Its token is 256 bits of
  `RandomNumberGenerator` output, returned once and stored only as a SHA-256 — so a leaked database
  yields no usable invite. `Auth__SignupSecretEnabled=false` then removes the shared secret from the
  signup path entirely.
- **The operator surface is an identity, not a second secret.** `/admin/*` is gated on `User.IsAdmin`
  resolved **from the database on every request**, never from the JWT — a year-long token must not
  carry a year-long privilege. So an admin's access dies exactly as any other token does, and demoting,
  suspending or deleting someone takes effect on their very next request. Each admin write logs one
  line carrying the actor's id, the action and the target user's or invite's id, and **never an email
  address**, for the same reason the request log carries no headers.
- **No admin route can strand the deployment.** Demoting, suspending or deleting the last
  administrator through `/admin/*` is a `409`, and `POST /admin/bootstrap` refuses once any admin
  exists — so neither route can be used to take a server over. Admins cannot suspend or delete their
  own account from `/admin/*`; they can stand down once somebody else can administer. The one way to
  end up with no administrator is a sole admin deleting *themselves* via `DELETE /auth/account`, which
  is deliberately not guarded — refusing it would make the last account on a server undeletable, and
  the state is recoverable because `POST /admin/bootstrap` starts answering again the moment no admin
  exists.
- The bootstrap secret is compared in fixed time, and the app refuses to start without one, without a
  signing key, or without a reachable database.
- The container runs as a non-root user (`app`, uid 1654) and Postgres publishes no host port.
- Secrets are never logged: the request log is method, path and status only — no query string, no
  headers and no body. Every secret the JSON API accepts arrives in a header or a body, and every one
  the portal accepts arrives in a form body, so none is ever in a path.

### What it deliberately does not do

- **No email verification.** An email address is an identifier here, not a verified one; the server
  sends no mail and never contacts the address. Anyone holding the signup secret, or an invite not
  bound to an address, can register any address they like.
- **No self-service password reset.** There is an *admin-issued* one — the portal's reset link, or `POST
  /admin/users/{id}/password-reset` — which you hand over out of band, but no
  `forgot my password` a user can drive alone, because that needs mail the server cannot send. A
  deployment with no administrator left at all falls back to the database; see
  [Recovering a lost password](#recovering-a-lost-password). Keep the signing key and a recent dump
  somewhere you can reach.
- **No quotas, and no visibility into anyone's data.** An admin can list users and act on the
  *accounts* — unlock, suspend, promote, reset, delete — and that is the whole surface. There is no
  role that can read another user's projects, tasks, entries or reports, and none that can cap what
  they store: one user can still fill the volume for everyone. **Every registered user is a full
  tenant.** Deletion is the only way an admin affects another user's rows, and it destroys them rather
  than exposing them.
- **No CORS policy.** The desktop app calls over `HttpClient`, not from a browser origin, and the
  portal is served from the API's own origin, so CORS is never consulted. Opening it "just in case"
  would only hand every page on the internet the ability to drive the API with a token it managed to
  read.
- **No refresh or token rotation.** Tokens are long-lived (`Jwt__ExpiryDays`, 365 by default) by choice,
  with revocation carrying the weight short expiry usually carries — see `JwtTokenService`'s XML doc for
  why that was chosen rather than overlooked. Rotating `Jwt__SigningKey` remains the blunt instrument
  that invalidates every token at once, across every account.
- **No IP-accurate rate limiting until you configure it.** Behind a proxy the limiter shares one
  bucket across the deployment unless `Network__TrustedProxies` names the hop — opt-in, because
  guessing would be the worse bug. See [Putting it behind TLS](#putting-it-behind-tls).
- **The lockout's residual denial of service is only half closed.** An admin can unlock an account, so
  a locked-out user asks someone rather than waiting on an operator with `psql`. But anyone who knows
  an address can still lock it again: the failure counter keys on the account alone, not on
  `(account, source address)`. Closing it properly needs a failure table and an eviction policy, and
  is deliberately not in this release.
- **Account deletion is immediate and irreversible.** `DELETE /auth/account` removes the time entries,
  tasks, projects, devices and the user row in one transaction. There is no grace period, no soft delete
  and no undo, and the only export is `/reports/*?format=csv`, which covers recorded time rather than
  the raw rows — take one before deleting, and keep your dumps.
- **No audit table.** What there is lives in the log and one table. Each admin write logs one
  `AdminAction` line naming the actor, the action and the target user or invite id; account deletion
  writes a single warning line carrying the user id and the per-table counts — never an email, for
  the same reason the request log records no headers. The `Invites` table keeps who issued and who
  redeemed each invite, by id, until that account is deleted. The log lines last only as long as you
  keep the container's logs, and nothing records sign-ins beyond each user's last login and each
  device's last-seen time.
## Development

Local development runs the app on the host against a Postgres container. `docker-compose.dev.yml` is the
dev-only file — a different port and a different volume from the production stack, so the two never
share state:

```powershell
docker compose -f docker-compose.dev.yml up -d      # localhost:15432, workledger/workledger

# The app fails fast if these are missing. `dotnet run` picks them up from
# Properties/launchSettings.json; set them explicitly for any other launch.
$env:Jwt__SigningKey = "<at least 32 bytes>"
$env:Bootstrap__Secret = "<any>"

dotnet build WorkLedger-Server.slnx
dotnet run --project src/WorkLedger.Server                 # http://localhost:5284
```

The dev connection string (`localhost:15432`) lives in `appsettings.Development.json`, so a Production
run has no localhost fallback to silently fall back to.

To work on the portal, turn it on for that run — `http://localhost` is allowed as a public base URL
because browsers treat it as a secure context:

```powershell
$env:Web__Enabled = "true"
$env:Web__PublicBaseUrl = "http://localhost:5284"
dotnet run --project src/WorkLedger.Server                 # then open http://localhost:5284/portal/setup
```

### Tests

```powershell
dotnet test WorkLedger-Server.slnx
dotnet test WorkLedger-Server.slnx --filter "FullyQualifiedName~ReportEndpointTests"
```

The data, auth, sync and report tests each spin up their own `Testcontainers.PostgreSql` container, so a
working Docker daemon is required; they never touch `docker-compose.dev.yml` or any real secret. The
`/health` test overrides `Database:MigrateOnStartup=false` and needs no database at all. CI
(`.github/workflows/build.yml`) restores, builds `Release`, runs the suite, and builds the production
image on every push and pull request.

### Schema changes

Run from `src/WorkLedger.Server/` — a design-time factory means no running app is needed:

```powershell
dotnet ef migrations add <Name> -o Data/Migrations
dotnet ef database update
```

The migration is applied automatically the next time the app boots (`Database:MigrateOnStartup`, true by
default), which is the simple choice for a single-instance deployment. It would have to become a separate
step before running more than one API instance against one database, since concurrent `Migrate()` calls
must not race — set `Database__MigrateOnStartup=false` and run `dotnet ef database update` (or
`migrations script`) as part of the deploy. See `src/WorkLedger.Server/Data/README.md`.

### Releases

Conventional Commits (`feat:` minor, `fix:` patch, `chore:`/`docs:`/`refactor:` no bump), one branch and
one PR per plan. A release is a `v`-prefixed tag:

```bash
git tag -a v0.1.0 -m "First self-hostable release"
git push origin v0.1.0
```

`.github/workflows/release.yml` then builds the image, pushes it to
`ghcr.io/dante286/workledger-api` (tagged `0.1.0`, `0.1`, `latest`, with the tag stamped into the
binary's informational version), and opens a GitHub release. Operators upgrade with
`docker compose pull && docker compose up -d`; nothing in CI deploys to a live environment.

## Cross-repo: the contract is vendored

`WorkLedger.Contracts` is authored here and the client keeps a **byte-identical copy** at
`Sync/Contracts/`, with the source commit recorded in its `Sync/Contracts/README.md`. There is no NuGet
package (a cross-repo `ProjectReference` was rejected: client CI checks out only its own repo). So
**changing `WorkLedger.Contracts` is a two-repo change** — re-take the copy and bump the provenance
commit, or the two sides drift silently, which is the exact failure the shared-C#-types design exists to
prevent. Nothing detects automatically that the copy has fallen behind; check with
`git log --oneline <pinned-commit>..HEAD -- src/WorkLedger.Contracts`.

**`WorkLedger.Contracts` holds what the desktop client speaks, and nothing else** (S11). The admin API's
request and response shapes and `ResetPasswordRequest` live in the server (`Auth/AdminDtos.cs`), because
the client never calls `/admin/*` or `/auth/password/reset`. The client's copy is pinned at **`eb743d4`**
(re-vendored 2026-09-30, WL-174) and nothing has changed in `WorkLedger.Contracts` since, so there is
no drift; `plans/S11-client-handoff.md` is the record of that re-vendor and of why none of it broke a
call site. Client 0.3.0 (WL-173) then dropped its own account UI: it signs in and opens `portalUrl` for
everything else.

Server work reads the client repo freely but does not commit to it (`plans/README.md`).

### Contract reconciliation

The DTOs in `src/WorkLedger.Contracts/Payloads/EntityPayloads.cs` are reconciled against the client's
`Models/Project.cs`, `Models/TaskItem.cs` and `Models/TimeEntry.cs` (checked 2026-08-11). Notably:
`Project` has no `IsArchived`; `TaskItem` has `Notes` and `IsCompleted` as well as `IsArchived`;
`TimeEntry` stores both `StartTime`/`EndTime` **and** a persisted `DurationSeconds` (not computed-only).
Parent links (`ProjectSyncId`, `TaskSyncId`) resolve by `SyncId`, never by the client's local
autoincrement ids.

`SyncChangeDto.PayloadJson` is **PascalCase** on the wire (`SyncJson.Options`): the client deserializes
with default, case-sensitive options, so switching to web/camelCase defaults would silently produce
all-default entities instead of throwing.
