namespace WorkLedger.Contracts.Auth;

/// <summary>
/// What a person signs in with, plus which install is asking. <c>DeviceId</c> is the client's
/// per-install GUID string — the same value it already sends as <c>Sync.SyncChangeDto.OriginDeviceId</c>
/// — because <b>credentials are user-level and tokens are device-level</b>: everything downstream needs
/// both halves, so the device identity has to arrive with the password rather than in a second call.
/// </summary>
public sealed record LoginRequest(string Email, string Password, string DeviceId, string? DeviceLabel);

/// <summary>
/// A minted token scoped to <c>(user, device)</c>, plus who it belongs to so a client can render the
/// signed-in identity without an immediate <c>/auth/whoami</c>.
/// <para>
/// Also what <c>POST /auth/password</c> returns: changing a password bumps the security stamp and so
/// kills every outstanding token including the caller's own, and handing back a replacement is what
/// stops that from looking like a bug.
/// </para>
/// </summary>
public sealed record LoginResponse(string Token, DateTime ExpiresAtUtc, UserSummary User);

/// <summary>
/// <see cref="CurrentPassword"/> is re-verified even though the caller already holds a valid token: a
/// stolen token must not be enough to take the account over.
/// </summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// Who this token belongs to. Carries the email and display name so a client can show the signed-in
/// identity from the one call it already makes.
/// </summary>
public sealed record WhoAmIResponse(int UserId, string Email, string? DisplayName, string DeviceId);

/// <summary>
/// One row of <c>GET /auth/devices</c>: something the user has signed in from, as a device-management
/// screen needs to show it.
/// <para>
/// <b><see cref="DeviceId"/> is the client's own per-install GUID string — the same value it sends as
/// <c>LoginRequest.DeviceId</c> and <c>Sync.SyncChangeDto.OriginDeviceId</c> — and it is what
/// <c>DELETE /auth/devices/{deviceId}</c> takes.</b> The server's internal row id is never on the wire,
/// exactly as synced entities are addressed by <c>SyncId</c> and never by their <c>Id</c>.
/// </para>
/// <para>
/// <see cref="IsCurrent"/> marks the device that made the request, so a client can label "this device"
/// without having to remember which GUID it installed with, and can warn before removing it (allowed,
/// and equivalent to signing this device out).
/// </para>
/// </summary>
public sealed record DeviceSummary(
    string DeviceId,
    string? Label,
    DateTime CreatedAtUtc,
    DateTime? LastSeenUtc,
    bool IsCurrent);

/// <summary>
/// The body of <c>DELETE /auth/account</c>. Both fields are gates on an irreversible, immediate and
/// total deletion — there is no grace period and no undo — and they guard against different mistakes.
/// <para>
/// <see cref="Password"/> is re-verified even though the caller already holds a valid token, for the
/// same reason <see cref="ChangePasswordRequest"/> does it: a leaked token must not be enough to destroy
/// an account.
/// </para>
/// <para>
/// <see cref="ConfirmEmail"/> must match the signed-in account's address (case-insensitively). That is
/// the typed-confirmation gesture a destructive action deserves, and it independently stops a client bug
/// — a stale token, a mixed-up session — from deleting an account the person was not looking at.
/// </para>
/// </summary>
public sealed record DeleteAccountRequest(string Password, string ConfirmEmail);

/// <summary>
/// The body of both <c>POST /auth/register</c> and <c>POST /auth/claim</c> — creating a user and
/// attaching credentials to the pre-S6 legacy row take the same inputs.
/// <para>
/// <b><see cref="InviteToken"/> is optional and only <c>/auth/register</c> reads it</b> (S10). It is the
/// alternative to the operator's <c>X-Bootstrap-Secret</c> header: either satisfies registration, and
/// which ones a given server will accept is advertised on <see cref="AuthCapabilitiesResponse"/>.
/// <c>/auth/claim</c> ignores it — claiming is the pre-user-accounts upgrade path, guarded by there
/// being exactly one credential-less user row, and an invite has nothing to say about that.
/// </para>
/// <para>
/// It rides in the body rather than in a header because, unlike the shared secret, it is <i>this
/// person's</i> credential for <i>this</i> request — the same reason a password is not a header either.
/// </para>
/// </summary>
public sealed record RegisterUserRequest(
    string Email, string Password, string? DisplayName, string? InviteToken = null);

/// <summary>
/// A created or claimed user. Deliberately carries <b>no token</b>: tokens are device-scoped and minted
/// by <c>POST /auth/login</c>, and registration is a user-level act with no device to bind one to.
/// </summary>
public sealed record UserSummary(int Id, string Email, string? DisplayName, DateTime CreatedAtUtc);

/// <summary>
/// What <c>GET /auth/capabilities</c> reports, so a client can render the truth about this server
/// instead of guessing at it: hide the registration UI when registration is switched off rather than
/// showing a form that can only end in a 403, offer "Claim this server" while
/// <see cref="HasUnclaimedAccount"/> holds, state the real minimum password length, and warn before a
/// sign-in attempt that would lock the account.
/// <para>
/// <b>These are configuration, not secrets</b>, which is why the endpoint is anonymous. A static policy
/// value tells an attacker only what five wrong guesses would have told them anyway.
/// </para>
/// <para>
/// <b><see cref="MaxFailedLogins"/> is deliberately not a remaining-attempts count.</b> A remaining
/// count could only be answered for an account that exists, so returning one would enumerate accounts
/// and undercut the uniform 401 that <c>POST /auth/login</c> goes out of its way to produce. Static
/// policy leaks nothing; per-account state leaks everything.
/// </para>
/// <para>
/// <b><see cref="RegistrationRequiresSecret"/> stopped being hardcoded in S10.</b> It now reports
/// <c>Auth:SignupSecretEnabled</c>, so a server where invites are the only way in says so instead of
/// advertising a secret that will be refused. <see cref="RegistrationAcceptsInvite"/> is its companion:
/// with both true a client offers either path, with only the invite true it asks for a token and hides
/// the secret field. Both can be true at once — they are two accepted credentials, not two modes.
/// <para>
/// <see cref="RegistrationAcceptsInvite"/> defaults to <c>false</c> purely so that adding it does not
/// change the positional constructor for existing callers; a server that has this field at all sets it
/// to true.
/// </para>
/// </para>
/// <para>
/// <b><see cref="LockoutMinutes"/> is whole minutes rounded up</b> from a window the server holds at
/// finer precision (see the server's <c>AuthOptions.LockoutMinutes</c>, which the test host sets to a
/// fraction of a minute so a real lockout window is waitable). Widening this field to a fractional type
/// would put a test affordance on the wire; a client only ever needs "come back in about N minutes".
/// </para>
/// <para>
/// <b><see cref="PortalUrl"/> is where this server's web portal lives, or null when it has none</b>
/// (S11). Every account operation — registration, password changes and resets, devices, deletion —
/// happens there, so a client that finds a value links to it instead of offering those screens itself,
/// and one that finds null says the server is managed by its operator. It is an absolute URL built from
/// the operator's configured public address, never from the request's <c>Host</c> header, so a client
/// can open it as given rather than guessing at paths. Defaulted to null for the same reason as
/// <see cref="RegistrationAcceptsInvite"/>: adding it leaves the positional constructor unchanged, and a
/// server older than S11 simply omits it.
/// </para>
/// </summary>
public sealed record AuthCapabilitiesResponse(
    bool RegistrationEnabled,
    bool RegistrationRequiresSecret,
    int MinPasswordLength,
    bool HasUnclaimedAccount,
    int MaxFailedLogins,
    int LockoutMinutes,
    bool RegistrationAcceptsInvite = false,
    string? PortalUrl = null);
