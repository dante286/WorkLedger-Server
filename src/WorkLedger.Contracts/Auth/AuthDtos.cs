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
/// attaching credentials to the pre-S6 legacy row take exactly the same three inputs.
/// </summary>
public sealed record RegisterUserRequest(string Email, string Password, string? DisplayName);

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
/// <b><see cref="LockoutMinutes"/> is whole minutes rounded up</b> from a window the server holds at
/// finer precision (see the server's <c>AuthOptions.LockoutMinutes</c>, which the test host sets to a
/// fraction of a minute so a real lockout window is waitable). Widening this field to a fractional type
/// would put a test affordance on the wire; a client only ever needs "come back in about N minutes".
/// </para>
/// </summary>
public sealed record AuthCapabilitiesResponse(
    bool RegistrationEnabled,
    bool RegistrationRequiresSecret,
    int MinPasswordLength,
    bool HasUnclaimedAccount,
    int MaxFailedLogins,
    int LockoutMinutes);
