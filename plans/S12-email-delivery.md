# Plan S12: Email delivery for invites, resets and self-service password recovery

**Tickets:** epic **WL-209** (*Email delivery: emailed invites and resets, self-service password recovery
(server)*). It has one `Task` per scope group and `Subtask`s per item, labelled `server` + `auth` + `email`,
plus `web` on the tickets that change portal pages. `Blocks` links between the tasks encode the build order,
and the epic is linked to S11's epic, WL-179, as "relates to". **There is no client-side half.** This plan
changes nothing in `WorkLedger.Contracts` (see item 19), so it needs no handoff and no re-vendor.

| Group | Items | Task | Subtasks |
|---|---|---|---|
| A. Mail transport | 1–7 | WL-210 | WL-215 `EmailOptions` + startup line, WL-216 MailKit sender, WL-217 outbound queue, WL-218 templates, WL-219 log hygiene |
| B. Operator-sent links | 8–11 | WL-211 | WL-220 email an invite, WL-221 email a reset, WL-222 `/portal/admin/email`, WL-223 `/admin/*` parity |
| C. Self-service recovery | 12–16 | WL-212 | WL-224 `/portal/forgot`, WL-225 eligibility + cooldown, WL-226 `SelfService` column + expiry |
| D. Security notice | 17 | WL-213 | — |
| E. Contract, tests, docs | 18–22 | WL-214 | WL-227 tests, WL-228 Mailpit fixture, WL-229 deployment + smoke step, WL-230 docs |

Build order is **A → B → C → D → E** (WL-210 → WL-211 → WL-212 → WL-213 → WL-214). See Sequencing.

> **Numbering.** S11's Out of scope named a portal reports page as an "S12 candidate". That was a suggestion,
> not a reservation. If reports are built first, renumber this plan rather than the other way around.

## Context

**This server sends no email, and that was a deliberate decision.** S10 item 13 made it the known limit:
"a dependency, a secret, and a deliverability problem, not a scope item". S11 kept it (items 18 and Out of
scope), and the code says the same in three places: `PasswordResetService`'s XML doc, `Credentials`' doc,
and the README's *What it deliberately does not do*. Every invite and reset link is handed over manually. The
admin copies it from the portal, which shows it exactly once, and sends it through some other channel.

That has two costs now that S11 shipped:

- **Every recovery needs an admin.** A user who forgets their password has to reach an admin, who issues a
  reset and passes it on. On a small deployment that admin is often the only person who could also be
  locked out.
- **The lockout DoS that S10 only partly closed.** Anyone who knows an address can lock that account by
  repeatedly failing to sign in. S10's remedy was "ask an admin". With mail, the account owner can recover
  without anyone else's help.

S10 named three objections. This plan answers each of them:

| S10's objection | This plan's answer |
|---|---|
| **A dependency** | One package, **MailKit** (MIT). Microsoft itself steers new code away from the built-in `System.Net.Mail.SmtpClient` (it is marked "not recommended for new development" and does not support modern protocols). MailKit is managed code with no native dependency, so the Alpine image needs nothing extra. |
| **A secret** | One optional secret, `Email__SmtpPassword`. It goes through `.env` exactly like the other three, and is required only once SMTP auth is configured. |
| **Deliverability** | **This plan does not solve it, and says so.** The server relays through the operator's own mail provider: SES, Postmark, Mailgun, Fastmail, Google Workspace, or their own Postfix. SPF, DKIM and DMARC are that provider's job. The README tells operators plainly that sending straight to recipients' mail servers from a VPS will land in spam. |

### The decisions this plan makes

1. **SMTP only, not a provider's HTTP API.** Every transactional mail provider offers an SMTP relay, and a
   self-hosted box may only have SMTP. One transport covers all of them. Adding a provider SDK would mean one
   integration per vendor, and none of them would be the one the operator happens to use.
2. **Off by default, and off means absent.** This is S11's `Web:Enabled` rule again. With `Email:Enabled`
   unset, no sender is registered, no queue runs, and no page or form field mentions email. An upgraded
   deployment behaves exactly as S11 until the operator turns email on.
3. **Email requires the portal.** Every link in a message goes to a portal page and is built from
   `Web:PublicBaseUrl`, which `PortalLinks` already treats as the only source of absolute links.
   `Email:Enabled=true` with the portal off is a configuration error at startup, and the error names both
   keys.
4. **An in-memory queue, not an outbox table.** This is the most important decision in the plan.
   - Today the database never holds a replayable token. `SingleUseToken` stores only a SHA-256 of each
     token, and both `InviteService` and `PasswordResetService` document that the raw token leaves the server
     exactly once.
   - An outbox table would store the rendered message, link included. That puts a live credential at rest in
     the same database, for as long as the row exists. It would quietly break a property three plans rely on.
   - So outgoing mail waits in a bounded in-process `Channel<T>`, drained by a `BackgroundService`.
   - **The cost is that mail pending at shutdown is lost.** That is acceptable because every message here can
     be reissued. An invite or reset can be issued again, and a forgotten-password request can be submitted
     again. Write this down; don't fix it.
   - One process is already the deployment model (S11 item 7), so a per-process queue needs no coordination.
5. **The portal still shows the link once, even when it also emails it.** The emailed copy is a convenience
   and does not replace the shown one. The admin keeps a fallback when mail is slow or lands in spam, and an
   operator whose SMTP setup breaks loses nothing they have today.
6. **Self-service recovery is portal-only.** There is no `POST /auth/password/forgot`. S11's rule is that
   new account features are portal-only and the JSON account routes are compatibility routes. A JSON version
   would be a new anonymous route that makes the server send mail, with no antiforgery protection in front
   of it.

### The relay this deployment will use: Azure Communication Services

The first deployment will relay through **Azure Communication Services (ACS) Email** over SMTP. This changes
no code, because ACS is configured through the same `Email__*` environment variables as any other relay. It
does decide which defaults have to work unchanged, and what the README's worked example shows:

| Setting | ACS value |
|---|---|
| `Email__SmtpHost` | `smtp.azurecomm.net` (Microsoft says to use the DNS name, never an IP address) |
| `Email__SmtpPort` | `587` (the default here) |
| `Email__SmtpSecurity` | `StartTls` (the default here). ACS requires TLS 1.2 or later. |
| `Email__SmtpUsername` | An **SMTP Username** created on the ACS resource and linked to a Microsoft Entra app registration. It can be free text or an email address on one of the resource's linked domains. |
| `Email__SmtpPassword` | A **client secret of that Entra app**. |
| `Email__FromAddress` | A sender address on a domain connected to the ACS resource: the Azure-managed `…azurecomm.net` domain, or a verified custom domain. |

Setting this up in Azure is operator work, and the README walks through it rather than the code handling it:

1. Create an Email Communication resource with a domain, and connect it to a Communication Services
   resource.
2. Register an Entra app and give it a role on the Communication Services resource. The built-in
   **Communication and Email Service Owner** role works. The least-privilege option is a custom role with
   `CommunicationServices/Read`, `CommunicationServices/Write` and `EmailServices/Write`.
3. Add an SMTP Username for that app, and wait until its status reads *Ready to use*.

**Three consequences for this plan:**

- **The SMTP password expires.** Entra client secrets have a fixed lifetime. When one expires, ACS refuses
  authentication with a 5xx reply. Item 3 treats 5xx as permanent and doesn't retry it, so every message
  fails until the secret is rotated, and each failure produces one log line. The README's ACS section says to
  calendar the secret's expiry and to rotate it by editing `.env` and running `docker compose up -d`.
  Item 10's test page is how the operator confirms the new secret works.
- **The from address must be one ACS accepts.** An address on a domain that isn't linked to the resource is
  refused. Startup can't check this, because only the relay knows its domains. This is the most likely
  first-run failure, and the test page shows ACS's own reply text, which names the problem.
- **ACS applies sending quotas**, and new resources and Azure-managed domains start with low limits. Volume
  here is a handful of messages a day, but a throttled send comes back as a 4xx reply, which item 3 retries.
  The README points at Microsoft's quota page rather than repeating numbers that will go out of date.

**Not chosen: the `Azure.Communication.Email` SDK with a managed identity.** It would remove the client
secret, but only when the container runs on Azure compute. It would also tie the server to one vendor, which
decision 1 rules out. If a later deployment runs on Azure and wants to drop the secret, the
`IEmailSender` seam in item 2 is where a second implementation would go.

## Scope

### A. Mail transport

1. **Settings: `EmailOptions`, resolved eagerly after `Build()`** next to `WebOptions`, with the same
   blank-counts-as-unset rule.

   | Key | Default | Notes |
   |---|---|---|
   | `Email:Enabled` | `false` | Off means absent (decision 2). |
   | `Email:SmtpHost` | — | Required when enabled. |
   | `Email:SmtpPort` | `587` | |
   | `Email:SmtpSecurity` | `StartTls` | `StartTls`, `SslOnConnect` (usually port 465), or `None`. See below. |
   | `Email:SmtpUsername` | — | Optional. Some relays authenticate by IP address. |
   | `Email:SmtpPassword` | — | **Secret.** Required when a username is set. |
   | `Email:FromAddress` | — | Required when enabled. Validated with `Credentials.LooksLikeEmail`. |
   | `Email:FromName` | `WorkLedger` | |
   | `Email:QueueCapacity` | `100` | See item 3. |

   - **`StartTls` maps to MailKit's `SecureSocketOptions.StartTls`, never `StartTlsWhenAvailable` or `Auto`.**
     Those two fall back to plaintext whenever the server doesn't advertise STARTTLS. Anyone positioned between
     this server and the relay can strip that advertisement and read every reset link. Requiring STARTTLS means
     a relay without it fails loudly. Put this reasoning in `EmailOptions`' XML doc, because "use `Auto`, it's
     more compatible" is exactly the change someone will make later.
   - **`None` is allowed, because a local Postfix relay on the compose network is a legitimate setup.** It is
     refused at startup if a username is also configured, because sending SMTP credentials in plaintext is
     never what an operator intended. With `None`, the startup log line (item 5) states that mail leaves this
     process unencrypted.
   - **No setting disables certificate validation**, and there will never be one. An operator with a relay
     that uses a self-signed certificate mounts their CA into the container's trust store. The README says
     how.
   - **Not checked at startup: whether the relay is reachable.** A mail server that is down for maintenance
     must not stop the container, because that would also stop sync for every device. Configuration is
     validated at startup; delivery is tested on demand (item 10).

2. **`IEmailSender`, with one production implementation: `SmtpEmailSender` on MailKit.**
   - It opens one connection per message. Volume here is a handful a day, and a pooled connection would be
     one more piece of state that can go stale.
   - Set MailKit's `Timeout` to 30 seconds, rather than its 2-minute default.
   - **Never attach MailKit's `ProtocolLogger`.** It records message bodies, which contain tokens, and the
     `AUTH` exchange, which contains the SMTP password. Put this on the class's XML doc.
   - Messages are **plain text only**. There's no HTML template, no tracking and no remote images. That means
     no templating dependency, nothing to escape, and nothing that leaks when a mail client loads remote
     content.
   - Every message carries an `Auto-Submitted: auto-generated` header (RFC 3834), which keeps vacation
     responders from replying to `FromAddress`.
   - Tests replace `IEmailSender` with a recording fake. The real implementation is covered by item 20's
     Mailpit test.

3. **`EmailOutbox`: a bounded `Channel<OutgoingEmail>` drained by `EmailDeliveryService : BackgroundService`.**
   - Capacity comes from `Email:QueueCapacity`, and the channel is created with `BoundedChannelFullMode.Wait`.
     Callers use `TryWrite` and never wait. A full queue returns false, and each caller decides what that
     means (items 8, 9 and 12).
   - **Retry only transient failures.** Retry up to three times with backoff (about 5 seconds, 30 seconds,
     then 2 minutes) for connection failures, timeouts and SMTP 4xx replies. A 5xx reply, such as a rejected
     recipient or a refused sender, is permanent and is not retried.
   - After the last attempt, the message is dropped and one log line is written (item 6).
   - **An `OutgoingEmail` may hold a token, and that's acceptable only because it lives in memory.** It must
     never be serialized, logged or persisted. Say so on the record type.

4. **Templates: one `EmailMessages` class** with a static method per message kind. It returns subject and
   body, and builds links only through `PortalLinks`. The message kinds are invite, admin-issued reset,
   self-service reset, password changed, and test message.
   - **Every message that carries a link also carries the token on its own line**, after "if the link doesn't
     open the form filled in, go to `{PublicBaseUrl}/portal/reset` and paste this code". Some mail security
     gateways rewrite links and may drop the fragment. S11 already gave every onboarding page a paste field,
     so the message should point people to it.
   - **S11's rule that no GET ever spends a token is what makes emailed links safe.** Corporate mail scanners
     open every link they see before the recipient does. Because the token travels in the fragment and only a
     POST spends it, scanning burns nothing. Note this on `EmailMessages` so nobody adds a link that spends a
     token on GET.

5. **Startup line, in both modes**, the same way `TrustedProxies` does it:
   - On: `"Email on: relaying via {Host}:{Port} ({Security}) as {FromAddress}."`
   - Off: `"Email off: invite and reset links are handed over by an admin."`

   `FromAddress` is the operator's own sending address, not a user's, so logging it doesn't break the rule
   against logging email addresses. The host and port aren't secret.

6. **Log hygiene: message kind, user id, and outcome only, never an address and never the SMTP server's
   text.** The rule that has been in place since S8 ("the user id and never the email") meets a new problem
   here. SMTP error replies often quote the recipient, for example
   `550 5.1.1 <bob@example.com>: Recipient address rejected`, and MailKit puts that text in
   `SmtpCommandException.Message`.
   - Delivery failures log the exception type, `StatusCode` and `ErrorCode`, and nothing else. Never pass the
     exception to the logger as `exception:`, because the console formatter would print its message.
   - The test-message page (item 10) is the one place the server's text is shown, and only to the admin who
     sent the message.

7. **Container concerns**, which can only fail inside the image (see `CLAUDE.md` § Deployment):
   - **Invariant globalization and internationalized domain names.** MimeKit encodes a non-ASCII domain to
     punycode using `IdnMapping`. Confirm its behaviour under `InvariantGlobalization`, which isn't assumed to
     work. If it throws, a user whose address has a non-ASCII domain must get "could not deliver", not a 500.
     `Credentials.LooksLikeEmail` already accepts such addresses, so they exist in real data. Item 21's smoke
     step covers this.
   - **CA certificates.** STARTTLS to a public relay needs the image's CA bundle. The `aspnet` Alpine base
     includes one. Verify it in the smoke step rather than trusting it.

### B. Operator-sent links (`PortalAdmin`)

8. **Invites page: "Email this invite".** A checkbox, shown only when email is on, and active only when the
   *Bound email* field has a value.
   - **Emailing an invite binds it to that address.** An open invite has no recipient, so it can't be emailed.
     The address it's sent to is the address it's bound to, so a forwarded invite still can't be redeemed by
     someone else.
   - The response shows the link once, as it does today (decision 5), plus one of two lines: "Queued for
     delivery to {address}", or "The mail queue is full; copy the link below instead."
   - The audit line stays `create-invite`. Add a separate `email-invite` line, with ids only, when the message
     is queued.
9. **Users page: "Issue reset and email it"** next to the existing "Issue reset link". It uses the same
   `AdminOperations.IssuePasswordResetAsync` (outdated resets are invalidated the same way, the audit line is
   the same, and so is the guard on users without credentials), then queues the admin-issued reset message to
   the target's `Email`. The page shows the link, and whether the message was queued, as in item 8.
   - The expiry is unchanged: `Auth:PasswordResetExpiryHours`. An admin-issued reset keeps its admin-issued
     lifetime whichever way it's delivered.
10. **`/portal/admin/email`: settings and a test message.** The page shows the resolved settings (host, port,
    security, from address, whether a username is set). It **never** shows the password.
    - It has one button, "Send a test message to me". It **sends to the signed-in admin's own address without
      going through the queue**, waits up to the 30-second timeout, and shows the result: "Delivered to the
      relay", or the failure with its SMTP status code and the server's reply text.
    - That reply text appears only on this page (item 6), because its reader is the person who configured the
      relay, and the address in it is their own.
    - This is how an operator checks their configuration. Startup deliberately doesn't (item 1). It is
      antiforgery-protected, has its own audit line (`send-test-email`), and doesn't appear when email is off.
11. **`/admin/*` parity, opt-in.** `/admin/*` is the operators' scripting interface, not a compatibility route
    (S11 item 23), so it gets the same option. The shapes involved live in `Auth/AdminDtos.cs`, which is server
    code, so this isn't a contract change.
    - The invite request gains `SendEmail` (default `false`, and requires `Email`).
    - `POST /admin/users/{id}/password-reset` accepts an optional body `{ "sendEmail": true }`.
    - Both responses gain `emailQueued`.
    - Existing scripts send neither field and see no change. With email off, `sendEmail: true` returns `409`
      with a body that says email isn't configured.

### C. Self-service password recovery (anonymous)

12. **`/portal/forgot`.** One field, *email*. It is anonymous, antiforgery-protected, and rate-limited under
    `AuthEndpoints.RateLimitPolicy`, sharing a per-IP bucket with sign-in. It returns `404` while email is off.
    The portal login page shows a "Forgot your password?" link only while email is on.
    - **The response is identical whatever the address.** It reads "If an account exists for that address,
      we've sent a link. It expires in N minutes." Unknown, suspended, credential-less, inside the cooldown, or
      dropped because the queue is full: same page, same status, same markup. This applies
      `CredentialCheck`'s uniform-failure rule to a new endpoint, and the reasoning is the same: an anonymous
      form must not reveal who has an account.
    - **The page does only constant work.** The POST validates the field's shape, writes the normalized
      address to the queue as a `ForgotPasswordRequest`, and renders the response.
      - The user lookup, the cooldown check, issuing the reset and sending happen in the background worker,
        not in the request.
      - If the request handler did the lookup and conditionally inserted a row, its response time would differ
        between known and unknown addresses. That reveals which addresses exist even though the page text
        doesn't.
      - Doing the work off the request path removes that difference instead of trying to equalize it.
13. **Who gets a message.** A user matched on `NormalizedEmail` who has a password hash and **is not
    suspended**.
    - **A suspended user gets nothing.** `PasswordResetService` deliberately doesn't lift a suspension (S10's
      reset-then-re-enable order). A reset sent to a suspended account would set a password the person still
      can't use, and the email would suggest it worked.
    - The unclaimed pre-S6 user has no email, so nothing can match it.
    - **The address on file was never verified** (see Out of scope). Under the signup-secret path, an account
      may have been registered with someone else's address. That person would receive the reset and could take
      over an account they never created. This doesn't harm them, and whoever registered the address had
      already given it away. Record it in the README's security section and don't build around it.
14. **Per-user cooldown: `Email:ResetCooldownMinutes`, default 5.** If a self-service reset was issued for
    this user within the window, the worker sends nothing. The page's response doesn't change.
    - Without a cooldown, the form could flood someone's inbox at whatever rate the per-IP limit allows from
      each address an attacker controls.
    - **The second reason is subtler.** Issuing a reset invalidates the user's outstanding ones (S10), so each
      repeat request would also cancel the link the real user is about to click. The cooldown limits that
      interference to once per window.
    - **Only self-service issues count toward the cooldown, and only self-service issues are subject to it.**
      An admin-issued reset never waits. A self-service issue does still invalidate an admin-issued link, as
      every new issue does; document that on the item 15 column.
15. **Schema: `PasswordResets.SelfService` (`bool`, not null, default `false`)**, one migration.
    - The cooldown needs to tell self-service issues from admin issues. `CreatedByUserId` can't do that:
      `null` already means "the issuing admin has since been deleted" (`PasswordReset`'s XML doc).
    - Every existing row gets `false`, which is accurate: every existing reset was issued by an admin.
    - The worker's issue path is `PasswordResetService.IssueAsync` with a new `selfService` parameter, rather
      than a second issue method. Keeping one path keeps "invalidate outstanding resets in the same
      transaction" in one place.
    - Audit: a `self-service-reset-issued` line with the user's id.
16. **Self-service expiry: `Auth:SelfServiceResetExpiryMinutes`, default 60.** This lives in `AuthOptions`, next
    to `PasswordResetExpiryHours`, because it is account policy.
    - It differs because the person who asked is the one who clicks, usually within a minute.
    - The 24-hour admin window exists because an admin-issued reset is handed over during a conversation that
      may span time zones. That reason doesn't apply here.
    - `PasswordReset.ExpiresAtUtc` already stores the expiry per row, so redemption needs no change.

### D. Security notice

17. **"Your WorkLedger password was changed."** Sent after commit by the services that change a password:
    `AccountSelfService` (change password) and `PasswordResetService.RedeemAsync`. It's sent whichever caller
    reached the service, which **includes the JSON compatibility route `POST /auth/password`**.
    - **This is deliberately a decision about where the rule lives, and it needs sign-off (see the end of this
      plan).** S11 says no new behaviour lands on compatibility routes. `CLAUDE.md` says to add a rule to the
      service and never to one of its callers.
    - The notice changes nothing on the wire: same status, same body. A password change made through the JSON
      route by someone who stole a device token is exactly the takeover this notice exists to catch.
    - Exempting the route would mean adding a caller flag to a service whose purpose is to have no caller
      flags.
    - The message contains no link or token, nothing that can be acted on from the message itself. It gives
      the time in UTC and says: "If this wasn't you, use *Forgot your password?* on the sign-in page, or
      contact your administrator."
    - Delivery is best effort through the queue. A failed or dropped notice never fails the password change.
    - It is not sent for sign-out-everywhere or for account deletion. The first changes no credential. After
      the second there is no account left to protect, and notifying an address after its account is deleted
      creates a record nobody asked for.

### E. Contract, tests, docs

18. **Fix docs that are now wrong as part of this plan, not later.**
    - `PasswordResetService`'s XML doc ("No email is sent").
    - `Credentials`' doc ("There is no SMTP here").
    - `PortalLinks`, which should note that links now also leave in email.
    - The README: *What it deliberately does not do*, the reset paragraph at "No email is sent by any of this",
      and the forgot-password line.
    - S10's and S11's plan text stays as written, apart from a one-line forward pointer to this plan in each
      Out of scope, as S10 received for S11.

19. **No contract change.** The desktop client signs in and nothing more (S11). A user who forgets their
    password goes to the portal, whose sign-in page now has the link. The client already gets `PortalUrl` from
    `/auth/capabilities`, so it can say "forgot your password? visit the portal" without knowing anything new.
    - A trailing, defaulted `bool PasswordRecoveryAvailable = false` on `AuthCapabilitiesResponse` would let
      the client show that line only when it would work. **That's deferred**, because it would turn a
      one-repo release into a two-repo one.
    - Audit: `git diff eb743d4..HEAD -- src/WorkLedger.Contracts` is expected to be empty. State it in the PR.

20. **Tests** (Testcontainers, next to the existing suites; portal tests use `PortalBrowser`; every test uses
    a recording `IEmailSender` except the Mailpit test).
    - **Email off (the default):**
      - The full existing suite passes with **no test modified**.
      - `/portal/forgot` and `/portal/admin/email` return `404`.
      - Neither the login, invites nor users page renders an email control.
      - No `EmailDeliveryService` is registered.
    - **Startup:** each of these fails startup, and the error names the key:
      - enabled with no host;
      - enabled with no from address;
      - enabled with the portal off (the error names both keys);
      - security `None` with a username;
      - a username with no password;
      - an unknown security value.
    - **Forgot:**
      - Known-active, unknown, suspended and credential-less addresses produce **byte-identical response
        bodies**, apart from the antiforgery token. Only known-active records a message.
      - A second request inside the cooldown records nothing.
      - The emailed link's token redeems at `/portal/reset`, and the row's expiry matches
        `SelfServiceResetExpiryMinutes`.
      - A self-service issue kills an outstanding admin-issued reset.
      - An admin-issued reset is unaffected by the cooldown.
      - `GET /portal/forgot` writes nothing.
      - A POST without antiforgery returns `400` and queues nothing.
      - The form is rate-limited with sign-in.
    - **Queue:**
      - A full queue makes the invite and reset pages say so and still show the link.
      - A full queue leaves `/portal/forgot`'s response unchanged.
      - A transient failure is retried.
      - A 5xx failure is not retried.
      - Each case's attempt count is asserted.
    - **Operator:**
      - An emailed invite is bound to the address, and its link registers exactly once.
      - "Email this invite" with no address is refused.
      - An emailed reset reaches the target's address and kills the target's earlier reset.
      - The test-message page sends to the signed-in admin only.
      - `/admin/*` with `sendEmail` matches the portal. Without it, the response is unchanged byte for byte.
    - **Notice:** a password change through the portal, through `POST /auth/password`, and through reset
      redemption each record exactly one notice, and that notice contains no token and no link.
    - **Log hygiene**, an extension of `PortalTokenHygieneTests` using `CapturedLogs`: across every flow
      above, including a fake sender that throws an `SmtpCommandException` whose message contains the
      recipient, **no raw token and no user's email address appears in any log line.**
    - **Real transport (one fixture):**
      - Run [Mailpit](https://mailpit.axllent.org/) as a Testcontainers generic container. Configure the real
        `SmtpEmailSender` against it, send an invite, and read it back through Mailpit's HTTP API.
      - Assert the recipient, the subject, a link containing the fragment, the separate paste-able token, and
        the `Auto-Submitted` header.
      - Then point `StartTls` at Mailpit's plaintext port and assert that delivery **fails** rather than
        downgrading to plaintext.

21. **Deployment.**
    - `docker-compose.yml`: pass every `Email__*` key through as blank (`${Key:-}`), with a comment. S11 found
      four keys that S10 forgot. `Auth__SelfServiceResetExpiryMinutes` goes in too.
    - `.env.example`: a commented *Optional: email* section. Mark the password as a secret, explain why
      `StartTls` is the default and why `None` exists, and say that sending direct-to-MX from a VPS ends up in
      spam.
    - `scripts/smoke-test.ps1`: an **optional** mail step, run when the operator passes `-WithMailpit`.
      - It brings up a Mailpit service from a small `docker-compose.mailpit.yml` override and enables `Email`
        against it with `None`.
      - It issues a reset with `sendEmail` through `/admin/*` for the smoke test's own user, and asserts that
        Mailpit received it.
      - It does the same for a user registered with a **non-ASCII domain**. That's the item 7 check, and the
        Alpine image is the only place it can fail.
      - The test suite can't cover this, because it runs on the host's globalization.

22. **Docs.**
    - README: an *Email* section covering enabling it, choosing a relay, the settings table, the test-message
      page, what is sent and when, that nothing is stored to retry after a restart, and the deliverability
      paragraph. **Use Azure Communication Services as the worked example**: the Azure setup steps, the
      `.env` block, and client-secret rotation (see *The relay this deployment will use*). Add one sentence
      saying any other SMTP relay works the same way through the same keys. Rewrite the onboarding and reset sections to show email first, with the copy-the-link flow
      still described.
    - `CLAUDE.md`: the architecture list gains the mail transport. The auth paragraph notes that portal
      recovery goes through `/portal/forgot`. Add one *Deployment* bullet: "mail is an in-memory queue by
      design; there is no outbox table because it would store raw tokens".
    - `plans/README.md`: the S12 row.

## Sequencing

- **A first, alone.** With `Email:Enabled` off, it changes nothing for any request, and "no test modified"
  is the check.
- **B before C.** The test-message page in B is how an operator confirms their relay works before turning on
  an anonymous form that sends mail. Building it first gives an end-to-end manual check for C as well.
- **C needs A's queue design specifically** (item 12's constant-work request path). Don't build C on a
  synchronous send "for now", because removing the timing channel later is a rewrite of the page.
- **D after C.** Both touch `PasswordResetService`, and D's notice is easier to test once C's sender fake is in
  place.
- **E last.** There's no handoff to write, but the docs pass and the smoke step come after the behaviour they
  describe is settled.

## Out of scope

- **Email verification at registration, and changing a user's email.** An invite already shows that an
  operator vouched for the address. The signup-secret path is the gap, and S10's answer is to turn that path
  off. Changing the address on an account is a feature of its own, and would need verification to be safe.
- **HTML email, templates, branding, and localization.** Plain text is enough for the content here. The
  runtime image's invariant globalization would make localized dates a separate project anyway.
- **Provider HTTP APIs** (SES, SendGrid, Postmark SDKs). Decision 1.
- **A persistent outbox, delivery tracking, bounces, and webhooks.** Decision 4 explains why the queue is
  in-memory. Bounce handling needs a webhook endpoint per provider.
- **Notifications beyond the password change**: new device signed in, invite redeemed (to the inviting
  admin), account suspended. Each one is reasonable, and each is a separate decision about what users
  should be told.
- **`PasswordRecoveryAvailable` on capabilities.** Item 19.
- **2FA, passkeys, and magic-link sign-in.** Mail makes magic links possible. They would also make the mailbox
  the only factor on every sign-in, which is a separate conversation.
- **Any client change.**

## Acceptance criteria

- With `Email:Enabled` unset, every request behaves exactly as it does on S11, and the full existing suite
  passes with no test modified.
- An operator can configure a relay, send themselves a test message from the portal, and see either "delivered"
  or the relay's own error, without reading container logs.
- An admin can email an invite or a reset from the portal or `/admin/*`, and the link is still shown once on the
  page as a fallback.
- A user who has forgotten their password can recover it from the portal sign-in page with no admin involved,
  and an anonymous caller cannot tell from response content or timing whether an address has an account.
- Repeated recovery requests for one address send at most one message per cooldown window.
- Every password change sends a notice to the account's address, whichever route made the change.
- No raw token is ever written to the database, and no token or user email address ever appears in a log line,
  including when the relay rejects a recipient.
- A relay that doesn't offer STARTTLS, under the default security setting, fails delivery instead of
  receiving mail in plaintext.
- `WorkLedger.Contracts` is unchanged, audited.

## Suggested commit messages

One PR per group, off `main`:

```
feat(email): add the SMTP transport, outbound queue and email settings
feat(email): email invites and password resets from the portal and /admin
feat(email): add self-service password recovery at /portal/forgot
feat(email): notify users when their password changes
docs(s12): document email delivery, pass its settings through compose, add the smoke step
```

## Decisions to confirm before building

These are the choices a reviewer is most likely to want made differently. Each one is a recommendation, not
something forced by the code:

1. **Item 17 notifies on the JSON compatibility route too.** The alternative is a caller flag on the service,
   which `CLAUDE.md`'s service-layer rule argues against.
2. **Decision 5, showing the link even when it is emailed.** The stricter alternative shows nothing and relies
   on email entirely. That takes away the fallback, and it doesn't remove the admin's ability to issue a reset
   and use it themselves.
3. **Defaults:** 60-minute self-service expiry, 5-minute cooldown, a queue of 100.
4. **No capabilities field** (item 19). That keeps this a server-only release.

## Prompt for Claude Code

> Read `CLAUDE.md`, `plans/S10-multi-user-operations.md` item 13, `plans/S11-web-portal.md`, and this plan
> first. The server currently sends no email, and invite and reset links are handed over by an admin. This
> plan adds SMTP delivery through **MailKit**, off by default behind `Email:Enabled`, and it requires the
> portal, because every link is built from `Web:PublicBaseUrl`.
>
> Start with group A as its own PR: `EmailOptions` resolved eagerly with the blank-is-unset rule, and
> `IEmailSender` with a MailKit implementation that **requires** STARTTLS by default (never
> `StartTlsWhenAvailable`/`Auto`), never disables certificate validation, and never attaches a
> `ProtocolLogger`. Add a bounded in-memory `Channel` drained by a `BackgroundService`. **There is no outbox
> table, because it would store raw tokens, which nothing in this database holds today.** Logs carry the
> message kind, user id and SMTP status code, and never an address or the SMTP server's reply text.
>
> Then build the operator side (email an invite, email a reset, `/portal/admin/email` with an inline test
> send), then `/portal/forgot`. Its POST only enqueues; the lookup, cooldown, issue and send happen in the
> worker, so neither the response content nor its timing reveals whether an address has an account. Add
> `PasswordResets.SelfService` and `Auth:SelfServiceResetExpiryMinutes`. Then add the password-changed notice
> from the services. Don't change `WorkLedger.Contracts`. Write the tests in item 20, including the Mailpit
> fixture and the no-downgrade test, add the compose pass-throughs and the optional smoke step, and fix the docs
> that say no email is sent. **Do not modify the client repo.**
