namespace WorkLedger.Contracts.Auth;

/// <summary>DeviceId is the client's per-install GUID string (see Sync.SyncChangeDto.OriginDeviceId).</summary>
public sealed record RegisterDeviceRequest(string DeviceId, string? Label);

public sealed record RegisterDeviceResponse(string Token, DateTime ExpiresAtUtc);

public sealed record WhoAmIResponse(int UserId, string DeviceId);

/// <summary>
/// The body of both <c>POST /auth/register</c> and <c>POST /auth/claim</c> — creating a user and
/// attaching credentials to the pre-S6 legacy row take exactly the same three inputs.
/// </summary>
public sealed record RegisterUserRequest(string Email, string Password, string? DisplayName);

/// <summary>
/// A created or claimed user. Deliberately carries <b>no token</b>: tokens are device-scoped and minted
/// by login (S7), and registration is a user-level act with no device to bind one to.
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
/// and undercut the uniform 401 that login (S7) goes out of its way to produce. Static policy leaks
/// nothing; per-account state leaks everything.
/// </para>
/// </summary>
public sealed record AuthCapabilitiesResponse(
    bool RegistrationEnabled,
    bool RegistrationRequiresSecret,
    int MinPasswordLength,
    bool HasUnclaimedAccount,
    int MaxFailedLogins,
    int LockoutMinutes);
