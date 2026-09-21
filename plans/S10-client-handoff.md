# S10 client handoff — invites, the operator surface, and password reset (WL-172)

**Server tickets:** epic [WL-150](https://mnisan.atlassian.net/browse/WL-150); this document is
[WL-172](https://mnisan.atlassian.net/browse/WL-172) under [WL-155](https://mnisan.atlassian.net/browse/WL-155).
**Client tickets:** none filed yet — see [What the client actually has to do](#what-the-client-actually-has-to-do),
which is deliberately short.

**Pin to record when re-vendoring:** **`1e343c8`** — the last commit to touch
`src/WorkLedger.Contracts/`. Server `main` has moved past it (docs and the rate limiter changed no
contract file), so pin the Contracts commit rather than `main`, exactly as S9 did.

**Client's current pin:** `3d27c86`, recorded in `Sync/Contracts/README.md`, re-vendored 2026-09-15
under WL-133. So the drift this document describes is **S10 and nothing else**. (`CLAUDE.md` in this
repo described the pin as `2065662` until this commit; that was stale by one re-vendor and is now
corrected.)

## The headline: this one is cheap

S9 was a cutover — types deleted, a record reshaped, every call site in `ApiSyncProvider` broken on
purpose. **S10 is not that.** Every contract change is additive, the two records that grew took their
new field last and with a default, and **the client compiles unchanged after the re-vendor.** That was
checked call site by call site, not assumed:

| Client call site | Why it still compiles |
|---|---|
| `Sync/ApiSyncProvider.cs:458` — `new RegisterUserRequest(email, password, displayName)` | Three positional args; `InviteToken` is the fourth and defaults to `null`. |
| `Tests/WorkLedger.Tests/SyncContractGuardTests.cs:294` — `new AuthCapabilitiesResponse(...)` | Six named args; `RegistrationAcceptsInvite` is the seventh and defaults to `false`. Its round-trip equality assertion still holds, because the field defaults the same way on both sides of the trip. |
| `Tests/WorkLedger.Tests/AuthPanelStateTests.cs:26` — `new(...)` with six positional args | Same. |
| `ViewModels/AuthPanelState.cs:90`, `Services/SyncCoordinator.cs:184` and the rest | Property reads and type references only. |

Nothing anywhere deconstructs either record positionally, which is the one pattern a trailing field
would have broken (`var (email, password, name) = request;`). Checked; there is none.

So the re-vendor is a copy, a pin bump, and a green build. Everything after that is optional.

## Step 0 — re-vendor `WorkLedger.Contracts`

Copy `src/WorkLedger.Contracts/` to the client's `Sync/Contracts/`, keeping the client's two local
conventions: the provenance header on each file and the explicit `using` directives (the client does
not enable `ImplicitUsings`). Then bump the pin in `Sync/Contracts/README.md` from `3d27c86` to
`1e343c8`.

- **One new file:** `Auth/AdminDtos.cs`. If the client is not going to build an admin surface, it can
  be vendored anyway (it is six dependency-free records and costs nothing) or left out with a line in
  `Sync/Contracts/README.md` saying so, the way `Reports/ReportDtos.cs` already is. **Do not vendor
  half of it** — pick one and record the choice.
- **`Reports/ReportDtos.cs` still stays un-vendored.** Unchanged standing decision, not drift.
- **`Sync/` and `Payloads/` are byte-identical to what the client already has** — see the audit below.
  Copy them anyway so the folder stays a faithful copy, but nothing in them needs reading.

## The contract inventory

### Added (7 records)

Six in the new `Auth/AdminDtos.cs`, one in `Auth/AuthDtos.cs`.

| Record | Shape | Used by |
|---|---|---|
| `ResetPasswordRequest` | `(string ResetToken, string NewPassword)` | `POST /auth/password/reset` — **the one a normal client might want** |
| `AdminUserSummary` | `(int Id, string Email, string? DisplayName, DateTime CreatedAtUtc, DateTime? LastLoginUtc, int DeviceCount, bool IsAdmin, bool IsLocked, bool IsDisabled)` | `GET /admin/users` |
| `AdminDeleteUserRequest` | `(string ConfirmEmail)` | `DELETE /admin/users/{id}` |
| `InviteRequest` | `(string? Email = null, int? ExpiresInDays = null, string? Note = null)` | `POST /admin/invites` |
| `InviteResponse` | `(string Token, InviteSummary Invite)` | `POST /admin/invites` |
| `InviteSummary` | `(int Id, string? Email, int? CreatedByUserId, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, DateTime? RedeemedAtUtc, int? RedeemedByUserId, string? Note)` | `POST` and `GET /admin/invites` |
| `PasswordResetTokenResponse` | `(string Token, DateTime ExpiresAtUtc)` | `POST /admin/users/{id}/password-reset` |

`AdminUserSummary.Id` is **the server's internal row id**, which is a deliberate exception to the rule
that ids on the wire are cross-device `SyncId` Guids. That rule exists so a caller cannot widen their
scope by guessing an identifier; an admin's scope is already every user, so there is nothing left to
widen, and a user has no cross-device identity to use instead. The XML doc on the record says so.

### Changed (2 records, both purely additive)

| Record | Change |
|---|---|
| `RegisterUserRequest` | gained a **fourth** parameter `string? InviteToken = null` |
| `AuthCapabilitiesResponse` | gained a **seventh** parameter `bool RegistrationAcceptsInvite = false` |

Both are last in the parameter list and both default, which is what makes the re-vendor a no-op for
the compiler. `RegistrationAcceptsInvite` defaults to `false` purely for that reason; a server that
has the field at all sets it to `true`.

### Removed or renamed: none.

### The sync wire format is unchanged — audited, not assumed

`git diff 3d27c86..HEAD -- src/WorkLedger.Contracts/Sync src/WorkLedger.Contracts/Payloads
src/WorkLedger.Contracts/Reports` is **empty**. Not "no behavioural change" — zero bytes, across all
three folders, since the exact commit the client is pinned at. `SyncJson.Options`' PascalCase policy,
the positional `SyncPushResponse.Results`, `SyncItemResult.AuthoritativeOperation`, the `>=` delete
accommodation and every payload shape are untouched.

The server's own S10 work touched no file under `Sync/`, and no merge rule in `SyncService` or
counting rule in `ReportService` changed. Verify it yourself with the command above before trusting
this paragraph; it is the one claim in this document that a future S11 could silently invalidate.

## What changed on the wire

### `GET /auth/capabilities` — one more field, and one that stopped lying

```jsonc
{
  "registrationEnabled": true,
  "registrationRequiresSecret": true,   // NO LONGER HARDCODED — now reports Auth:SignupSecretEnabled
  "minPasswordLength": 12,
  "hasUnclaimedAccount": false,
  "maxFailedLogins": 5,
  "lockoutMinutes": 15,
  "registrationAcceptsInvite": true     // new
}
```

`registrationRequiresSecret` was hardcoded `true` before S10 and is now the real answer. **This is the
only behavioural change to an endpoint the client already calls**, and it is the one worth reading
twice: a server with `Auth__SignupSecretEnabled=false` now reports `false`, and
`AuthPanelState.ShowSignupSecret` will correctly stop offering the field. That is the desired
behaviour and needs no client change — but a client that currently *assumes* the secret is always
wanted would be showing a field that can only produce a `401`.

The two flags are **not** a mode switch. Both true means both credentials are accepted; a client can
offer either path. Older servers omit `registrationAcceptsInvite`, which deserializes to `false` — so
"this server does not do invites" is the correct reading of its absence.

### `POST /auth/register` — an alternative credential

The body may now carry `inviteToken`. The gate is: a valid `X-Bootstrap-Secret` header **or** a valid
unredeemed, unexpired invite. Status codes the client has to distinguish:

| Status | Means |
|---|---|
| `403` | registration is closed outright — invites included |
| `401` | neither credential was accepted |
| `400` | bad email or password |
| `409` | the email is taken, **or** the invite is expired, already spent, or addressed to somebody else |

That last `409` is frank rather than opaque on purpose: the caller already holds a one-time token
addressed to them, so there is nothing to hide, and a vague `401` would send them back to whoever
issued it. The message distinguishes the cases; the status does not. A client showing the server's
message is doing the right thing.

### `POST /auth/password/reset` — new, anonymous, and the one worth building

```
POST /auth/password/reset        { "resetToken": "...", "newPassword": "..." }   -> 204
```

Anonymous, rate-limited with the other credential endpoints, and **it returns no token**. Tokens are
scoped to a `(user, device)` pair and this call names no device, so every device signs in again — the
correct outcome for a credential that had been lost, and the deliberate difference from
`POST /auth/password`, which re-mints for the calling device because the caller was already signed in
on it.

- `400` for a token that is unknown, expired or already spent — **all three answer the same `400`
  with the same message**, on purpose. Do not try to tell them apart; the server will not help.
- `400` for a new password below `minPasswordLength`, and in that case the token **stays spendable**,
  so the user can try again with a longer one rather than having to ask for another token.
- After `204` the client is signed out everywhere. It must re-authenticate with
  `POST /auth/login`, not assume its existing token survived.

### `/admin/*` — entirely new, and entirely optional

Twelve routes, all behind an `admin` authorization policy. `401` without a token, `403` with a
non-admin's. Full table in this repo's `README.md` under *Endpoints*. The client needs **none** of
them to keep working.

## What the client actually has to do

**Required: nothing but the re-vendor.** The build stays green and every existing flow behaves the
same. Land it as its own commit anyway, so the pin bump is attributable.

**Worth doing, in this order:**

1. **Accept an invite token at registration** (`RegisterUserRequest.InviteToken`). One optional field
   on the create-account form, shown when `registrationAcceptsInvite` is true. This is the whole
   point of S10 from a user's perspective — it is how somebody gets an account without being handed
   the operator's secret. `ApiSyncProvider.CreateAccountAsync` currently takes
   `(email, password, displayName, bootstrapSecret)`; it needs one more optional parameter, and the
   comment above it saying "register and claim take the same three inputs and the same secret header"
   stops being true for register.
2. **Redeem a password reset** (`POST /auth/password/reset`). A "I have a reset token" path from the
   signed-out state. Note the sign-out-everywhere consequence above: on `204` the client must clear
   whatever token it holds and go back through login.
3. **Nothing else.** An admin UI is a real feature and a separate decision; the DTOs are vendored so
   it is *possible*, not so it is expected. The server's own README documents the operator flow as
   `curl`, and that is a legitimate permanent answer for a self-hosted box.

## Things that did not change, and are worth not re-deriving

- **Tenancy.** `UserId` still comes from the token and nothing else. No route added in S10 reads a
  tenancy scope from a body or a URL — `/admin/*` takes target ids by design and is gated on a
  database-resolved `IsAdmin`, which is a different thing from widening a caller's own scope.
- **Token lifetime and revocation.** Unchanged: `Jwt:ExpiryDays`, no refresh, the `stamp` claim, the
  `Auth:StampCacheSeconds` lag with eviction on the paths that revoke.
- **`/auth/claim` and `UnclaimedUser`.** Untouched. `/auth/claim` ignores both
  `Auth:SignupSecretEnabled` and `REGISTRATIONS_ALLOWED`, deliberately, so an operator can always take
  ownership of pre-existing data.
- **Two new 401 sources, neither distinguishable and neither new to the client's logic.** A
  *suspended* account's existing tokens are refused, and its login answers the same `401` as a wrong
  password — deliberately identical, so suspension does not become a way to discover who has an
  account. A client already treats `401` as "signed out, re-authenticate", which is the right
  behaviour here; there is nothing to add.

## Also updated by this ticket

- `plans/README.md` — the S10 row and the handoff pointer.
- `CLAUDE.md` — the *Known drift* paragraph, which still described the pin as `2065662` and the drift
  as S6–S8. Both were a re-vendor out of date.
