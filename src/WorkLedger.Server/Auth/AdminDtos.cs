using WorkLedger.Contracts.Auth;

namespace WorkLedger.Server.Auth;

// The admin JSON API's shapes, and the body of POST /auth/password/reset. They lived in
// WorkLedger.Contracts until S11 and moved here because that library holds what the desktop client
// speaks and nothing else: the client never calls /admin/* or /auth/password/reset, and its vendored
// copy was pinned at 3d27c86 — before every record in this file existed — so the move broke no one. It
// re-vendored at eb743d4 without them (WL-174). See plans/S11-client-handoff.md.

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

/// <summary>
/// What <c>POST /admin/users/{id}/password-reset</c> returns. <b><see cref="Token"/> appears here and
/// nowhere else, ever</b> — the server stores only a SHA-256 of it — so this response is the single
/// opportunity to capture it and hand it to the person who needs it.
/// <para>
/// <b>There is no email in this response, and no password.</b> The admin is being given a one-time door
/// to pass along, not the user's credential: what happens on the far side of it is between the user and
/// <c>POST /auth/password/reset</c>. <see cref="ExpiresAtUtc"/> is here so the admin can say how long
/// the person has, which is the first thing they will be asked.
/// </para>
/// </summary>
public sealed record PasswordResetTokenResponse(string Token, DateTime ExpiresAtUtc);

/// <summary>
/// The body of <c>POST /auth/password/reset</c>: a token an admin issued out of band, and the password
/// its holder has chosen.
/// <para>
/// <b>No email, and no current password.</b> The token identifies the account by itself — asking for an
/// address as well would add a second thing to get wrong without adding a second factor, since the
/// token is already the whole credential. And the point of a reset is that the current password is what
/// the person no longer has.
/// </para>
/// <para>
/// The reply is <c>204</c> and carries <b>no token</b>. Tokens here are scoped to a <c>(user, device)</c>
/// pair and this call names no device; every device signs in again, which is the correct outcome for a
/// credential that had been lost. That is the deliberate difference from <c>POST /auth/password</c>,
/// which re-mints for the calling device because the caller was already signed in on it.
/// </para>
/// </summary>
public sealed record ResetPasswordRequest(string ResetToken, string NewPassword);
