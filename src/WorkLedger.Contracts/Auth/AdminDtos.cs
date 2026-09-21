namespace WorkLedger.Contracts.Auth;

/// <summary>
/// One row of <c>GET /admin/users</c> — every user on the deployment, as an operator's management
/// screen needs to see them.
/// <para>
/// <b><see cref="Id"/> is the server's internal row id, and that is a deliberate exception to the rule
/// that ids on the wire are cross-device <c>SyncId</c> Guids.</b> That rule exists so a caller cannot
/// widen their scope by guessing an identifier; an admin's scope is already every user, so there is
/// nothing left to widen. A user has no cross-device identity to expose in its place, and this very
/// response hands the ids out — inventing an opaque one would add a lookup table to protect nothing.
/// </para>
/// <para>
/// <see cref="IsLocked"/> is derived, not stored: it is whether the user's lockout window has yet to
/// expire at the moment of the query, which is the question an operator is actually asking when
/// someone reports being unable to sign in.
/// </para>
/// </summary>
public sealed record AdminUserSummary(
    int Id,
    string Email,
    string? DisplayName,
    DateTime CreatedAtUtc,
    DateTime? LastLoginUtc,
    int DeviceCount,
    bool IsAdmin,
    bool IsLocked,
    bool IsDisabled);

/// <summary>
/// The body of <c>DELETE /admin/users/{id}</c>. The typed-confirmation gesture that
/// <see cref="DeleteAccountRequest"/> uses, minus the password gate — an admin deleting someone else's
/// account does not have their password, and requiring their own would confirm identity without
/// confirming <i>which account</i> is about to be destroyed, which is the mistake actually worth
/// guarding against here.
/// <para>
/// <see cref="ConfirmEmail"/> must match the <b>target</b> user's address, case-insensitively. So a
/// stale user list, or an off-by-one in a client's table, cannot delete the wrong person: the id and
/// the address have to agree.
/// </para>
/// </summary>
public sealed record AdminDeleteUserRequest(string ConfirmEmail);

/// <summary>
/// The body of <c>POST /admin/invites</c>. Every field is optional — an invite with none of them set is
/// a valid open invite on the server's default expiry, which is the common case when an operator is
/// about to paste a link into a chat window.
/// </summary>
/// <param name="Email">
/// Binds the invite to one address, compared case-insensitively at redemption. Null leaves it
/// <b>open</b>: anyone holding the token may redeem it, once. Binding guards against a forwarded token
/// being spent by the wrong person; it is not authentication, since whoever redeems it still chooses
/// their own password.
/// </param>
/// <param name="ExpiresInDays">
/// Overrides the server's <c>Auth:InviteExpiryDays</c> default for this one invite. There is no value
/// meaning "never" — an invite that cannot expire is a standing credential, which is the thing invites
/// exist to stop being.
/// </param>
/// <param name="Note">Free-form, for the operator's own benefit when reading the list later.</param>
public sealed record InviteRequest(string? Email = null, int? ExpiresInDays = null, string? Note = null);

/// <summary>
/// What creating an invite returns. <b><see cref="Token"/> appears here and nowhere else, ever</b> — the
/// server stores only a SHA-256 of it, so this response is the single opportunity to capture it. Listing
/// invites afterwards returns <see cref="InviteSummary"/> alone.
/// </summary>
public sealed record InviteResponse(string Token, InviteSummary Invite);

/// <summary>
/// One invite as <c>GET /admin/invites</c> reports it: everything except the token itself.
/// <para>
/// <see cref="RedeemedAtUtc"/> and <see cref="ExpiresAtUtc"/> are given raw rather than reduced to a
/// status word, because the two dead states are not interchangeable — "already used" and "ran out" send
/// the person who asks about it somewhere different — and a client can render whichever it prefers.
/// </para>
/// <para>
/// <see cref="CreatedByUserId"/> and <see cref="RedeemedByUserId"/> are null when the account in
/// question has since been deleted. The invite deliberately outlives both.
/// </para>
/// </summary>
public sealed record InviteSummary(
    int Id,
    string? Email,
    int? CreatedByUserId,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? RedeemedAtUtc,
    int? RedeemedByUserId,
    string? Note);
