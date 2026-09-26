# S11 client handoff — the web portal, and the contract it leaves behind (WL-208)

**Server tickets:** epic [WL-179](https://mnisan.atlassian.net/browse/WL-179); this document is
[WL-208](https://mnisan.atlassian.net/browse/WL-208) under [WL-185](https://mnisan.atlassian.net/browse/WL-185).
**Client tickets:** epic [WL-173](https://mnisan.atlassian.net/browse/WL-173), client plan 12
(`WorkLedger-Client/plans/12-account-lifecycle-to-web-portal.md`). Its re-vendor is WL-174. **This document is
authoritative for that re-vendor** and outranks the client plan wherever they disagree.

**Pin to record when re-vendoring:** **`eb743d4`**, the last commit to touch `src/WorkLedger.Contracts/`.
Server `main` will move past it, because the S11 docs commit and everything after it change no contract file.
So pin the Contracts commit rather than `main`, exactly as S9 and S10 did. (S11's PR is merged with a merge
commit, like every S-plan PR before it, so the hash survives the merge. Confirm with
`git log -1 --format=%h -- src/WorkLedger.Contracts` on server `main`. If that ever prints something else,
trust it over this line.)

**Client's current pin:** `3d27c86`, recorded in `Sync/Contracts/README.md` and re-vendored 2026-09-15 under
WL-133. **The client never re-vendored at S10's `1e343c8`**, so this handoff covers **S10 and S11 together** and
supersedes [S10-client-handoff.md](S10-client-handoff.md)'s step 0. That document remains the record of what S10
changed on the wire (the invite gate on `/auth/register`, the no-longer-hardcoded `registrationRequiresSecret`).
Its vendor-or-decline question about `AdminDtos.cs` no longer arises, because the file is no longer in the
library.

## The headline: three trailing parameters, and nothing else

Between the client's pin and this one, the **entire** difference in `WorkLedger.Contracts` is three
parameters added to two records in `Auth/AuthDtos.cs`. Each is last and defaulted:

```
git diff 3d27c86 eb743d4 --stat -- src/WorkLedger.Contracts
 src/WorkLedger.Contracts/Auth/AuthDtos.cs | 42 ++++++++++++++++++++++++++++---
```

| Record | Parameter | From | Default |
|---|---|---|---|
| `RegisterUserRequest` | 4th: `string? InviteToken` | S10 | `null` |
| `AuthCapabilitiesResponse` | 7th: `bool RegistrationAcceptsInvite` | S10 | `false` |
| `AuthCapabilitiesResponse` | 8th: `string? PortalUrl` | **S11** | `null` |

Everything else in the diff is XML doc text. **No record was added, removed or renamed, and no parameter was
reordered.** S10's seven added records (`AdminDtos.cs` and `ResetPasswordRequest`) were in the library at
`1e343c8` and are **not** at `eb743d4`. See [Moved out](#moved-out-and-why-it-costs-the-client-nothing).

### The client compiles and passes unchanged. That was built and run, not only read

A scratch copy of client `main` @ `55cd4c8` had its `Sync/Contracts/AuthDtos.cs` replaced with the file at
`eb743d4` (keeping the provenance header and `using System;`) and nothing else. On that copy:

- `dotnet build WorkLedger-Client.slnx` gives **0 errors, 0 warnings**.
- `SyncContractGuardTests`, `AuthPanelStateTests` and the `ApiSyncProvider` tests all pass (**104/104**).

The client repo itself was not touched. The call sites, and why each survives:

| Client call site | Why it still compiles |
|---|---|
| `Sync/ApiSyncProvider.cs:458`: `new RegisterUserRequest(email, password, displayName)` | Three positional args. `InviteToken` is fourth and defaults. (Client plan 12 item 3 deletes this call anyway.) |
| `Tests/WorkLedger.Tests/SyncContractGuardTests.cs:294`: `new AuthCapabilitiesResponse(...)` with six named args | Both new parameters default. The round-trip equality still holds, because each field defaults the same on both sides of the trip. |
| `Tests/WorkLedger.Tests/AuthPanelStateTests.cs:26`: `new(...)` with six positional args | Same. |
| `Sync/ApiSyncProvider.cs:113/398/405`, `Services/SyncCoordinator.cs:224`, `ViewModels/AuthPanelState.cs`, `ViewModels/MainWindowViewModel.Sync.cs:136` | Type references and property reads only. |

Nothing deconstructs either record positionally or pattern-matches on its shape, which is the one thing a
trailing parameter could break. That was checked, and there is none.

**On the wire:** the server writes `"portalUrl"` (camelCase), and the client reads auth responses with
`SyncJson.Options`, which is `PropertyNameCaseInsensitive`. A pre-S11 server omits the property, and it
deserializes to `null`, which is the correct reading ("this server has no portal").

## Step 1: re-vendor `WorkLedger.Contracts` at `eb743d4`

Copy `src/WorkLedger.Contracts/` into the client's `Sync/Contracts/`, keeping the client's local conventions:
the provenance header on each file, explicit `using` directives (the client does not enable `ImplicitUsings`),
and the flat layout. Bump the pin in `Sync/Contracts/README.md` from `3d27c86` to `eb743d4`.

- **Only `AuthDtos.cs` changes.** There is no `Auth/AdminDtos.cs` to vendor or decline.
- **`Sync/`, `Payloads/` and `Reports/` are byte-identical** to what the client already has (see the audit
  below). Copy them anyway so the folder stays a faithful copy; nothing in them needs reading.
- **`Reports/ReportDtos.cs` stays un-vendored.** That is an unchanged standing decision, not drift.
- **Land it as its own commit, green on its own**, before any removal in client plan 12, so the pin bump is
  attributable and the "compiles unchanged" claim above is proven in the client's own CI.
- Add the standing note client plan 12 item 1 describes: the four records in
  [Kept alive](#kept-alive-by-the-compatibility-routes) are still vendored because upstream still carries them,
  and that is not drift.

## The contract inventory

### Changed: 2 records, purely additive

`RegisterUserRequest` and `AuthCapabilitiesResponse`, as tabled above. S10's two parameters are described in
[S10-client-handoff.md](S10-client-handoff.md#changed-2-records-both-purely-additive). S11's is new here:

**`AuthCapabilitiesResponse.PortalUrl`** is where this server's web portal lives, or `null` when it has none.

- **It is absolute and ready to open**, e.g. `https://ledger.example.com/portal`. The server builds it from the
  operator's `Web:PublicBaseUrl`, which is required whenever the portal is on and must be `https://` (or
  `http://` for a loopback host). It is **never** built from the request's `Host` header. So the client opens it
  as given and never derives it from its own `ApiEndpoint`. A server that did not advertise a portal may not
  have one.
- **`null` means one of two things**, and the client need not tell them apart: a pre-S11 server, or the operator
  has not set `Web:Enabled=true`. Either way there is no page to send anyone to.
- **It is the portal's entry point, and the only path that is contract.** An anonymous browser at it is sent to
  sign in, and a signed-in one lands on account home, which links to devices, password and sign-out, account
  deletion and (for admins) the Administration pages. Deeper paths exist (`/portal/login`,
  `/portal/account/devices` and others) but are not promised. Link to `PortalUrl`, not to something built from
  it.
- The client should still validate the scheme before launching it (client plan 12 item 5). The server's own
  validation is not something a client should have to trust.

### Moved out, and why it costs the client nothing

S11 established a scope rule for the library: **`WorkLedger.Contracts` holds what the desktop client speaks,
and nothing else.** Shapes used only by server-side surfaces live in the server. Applying the rule moved seven
records to `src/WorkLedger.Server/Auth/AdminDtos.cs` (namespace `WorkLedger.Server.Auth`):

| Record | Used by |
|---|---|
| `AdminUserSummary`, `AdminDeleteUserRequest` | `GET /admin/users`, `DELETE /admin/users/{id}` |
| `InviteRequest`, `InviteResponse`, `InviteSummary` | `/admin/invites` |
| `PasswordResetTokenResponse` | `POST /admin/users/{id}/password-reset` |
| `ResetPasswordRequest` | `POST /auth/password/reset` |

**This looks like a removal and is not a break,** for two independent reasons:

1. **The client has never compiled against any of them.** All seven were added in S10 (`fb2e7cf`, `dfc37e7`,
   `1e343c8`), after the client's pin at `3d27c86`, and the client never re-vendored at S10. A record no client
   build has ever contained can move without breaking anyone.
2. **The client will never call their routes.** Client plan 12 removes all account lifecycle from the desktop
   app. `/admin/*` is the operators' scripting interface (with the portal's Administration pages beside it), and
   password resets are redeemed in the portal at `/portal/reset`.

The routes themselves are unchanged. Only the C# type moved; the JSON shapes are exactly what S10 shipped.

### Kept alive by the compatibility routes

Everything the client **has** vendored stays in the library, including the records client plan 12 stops using:

| Record | Kept alive by |
|---|---|
| `RegisterUserRequest` | `POST /auth/register`, `POST /auth/claim` |
| `ChangePasswordRequest` | `POST /auth/password` |
| `DeviceSummary` | `GET /auth/devices` |
| `DeleteAccountRequest` | `DELETE /auth/account` |

These routes are now documented **compatibility routes** (see `AccountEndpoints`' class doc). The full list is
`POST /auth/register`, `POST /auth/claim`, `POST /auth/password`, `POST /auth/password/reset`,
`GET /auth/devices`, `DELETE /auth/devices/{deviceId}`, `POST /auth/logout-all` and `DELETE /auth/account`.

- **They keep working unchanged**, now as thin callers of the same services the portal calls, so v0.1.x clients
  in the field and operators' `curl` scripts are unaffected.
- **No new behaviour lands on them**, and new account features are portal-only.
- **Removing them is a wire-breaking change** for every client build that still calls them, so they and the
  four records go at the next release that breaks the wire on purpose, not before. Until then they are
  **server-only once client plan 12 ships**: vendored, compiled, uncalled, and not drift.

`POST /auth/login`, `GET /auth/capabilities` and `GET /auth/whoami` are **not** compatibility routes. They are
what the client still speaks, and `LoginRequest`, `LoginResponse`, `WhoAmIResponse`, `UserSummary` and
`AuthCapabilitiesResponse` are its live contract.

### The sync wire format is unchanged. Audited, not assumed

```
git diff 3d27c86 eb743d4 -- src/WorkLedger.Contracts/Sync src/WorkLedger.Contracts/Payloads src/WorkLedger.Contracts/Reports
git diff 1e343c8 eb743d4 -- src/WorkLedger.Contracts/Sync src/WorkLedger.Contracts/Payloads src/WorkLedger.Contracts/Reports
```

Both are **empty**: zero bytes, across all three folders, from the commit the client is pinned at and from
S10's pin. `SyncJson.Options`, the positional `SyncPushResponse.Results`, `SyncItemResult.AuthoritativeOperation`,
the `>=` delete accommodation and every payload shape are untouched. No merge rule in `SyncService` or counting
rule in `ReportService` changed in S11. Re-run the commands before trusting this paragraph.

## What the client does now

Client plan 12 is the design; this is only what the server side guarantees it.

- **Accounts are made and managed in the browser.** First-run setup, sign-up from an invite link, password
  change and reset, devices, sign-out-everywhere, account deletion and every operator action are portal pages.
  The desktop client signs in with `POST /auth/login` and syncs. Nothing else on the auth surface is needed.
- **There is no open sign-up in the portal.** Registration needs an invite link from an administrator, or the
  signup secret on a server that still accepts it. So "No account? Ask this server's administrator for an
  invite" is more accurate copy than one suggesting the person can create an account themselves.
- **The browser never becomes a device.** A portal session creates no `Device` row, so it never appears in
  `GET /auth/devices`, and signing out of the portal signs out only that browser, never a desktop install.
- **Everything the portal does to an account reaches the client as a `401`**, on the very next sync, not after a
  cache TTL. That covers a password change or sign-out-everywhere in the portal, a reset redeemed there,
  removing this install on the Devices page, an admin suspending or deleting the account, and deleting it
  yourself. There is no new status code and no new body. The client already treats a sync `401` as "signed out,
  sign in again", which is right for all of them. Client plan 12's `SessionRevoked` copy lists these causes.
- **The lockout is one budget per account, shared with the portal.** Portal sign-in goes through the same
  credential check as `POST /auth/login`, so failed portal attempts count towards the lockout a device sees, and
  vice versa. A client showing `423` may be seeing failures that happened in a browser. A reset redeemed in the
  portal clears the lockout, which is why client plan 12 item 6's "I've reset my password" works: the next
  device sign-in with the new password succeeds.
- **The server stays useful without the client, and the client without the server.** Nothing on the server
  requires a desktop install to exist, and nothing in this handoff asks the client to require a server.

## Things that did not change, and are worth not re-deriving

- **Tokens.** `POST /auth/login` is still the only source. Still `Jwt:ExpiryDays`, still no refresh, still
  revoked by the `stamp` claim, with `Auth:StampCacheSeconds` as the lag only for a revocation made by another
  process.
- **Tenancy.** `UserId` comes from the credential and nothing else. Portal pages scope on the session cookie's
  `user_id` exactly as JSON routes scope on the token's.
- **The two credentials never cross.** A portal cookie is refused by every JSON route, including `/sync/*`, and
  a bearer token is refused by every portal page. Nothing the client sends changes because the portal exists.
- **`/auth/capabilities`' other seven fields** answer exactly as before, portal on or off. That is asserted by
  a test, not assumed.

## Also updated by this ticket

- `plans/README.md`: the S11 rows marked done, this document linked, and the handoff's result.
- `CLAUDE.md`: the *Known drift* paragraph now points here, names `eb743d4`, and describes the drift as the three
  trailing parameters.
- `plans/S10-client-handoff.md` already carries its supersession note (added with the S11 plan in `addb197`).
