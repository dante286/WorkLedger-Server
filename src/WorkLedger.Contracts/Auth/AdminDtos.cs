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
