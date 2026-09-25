namespace WorkLedger.Server.Data.Entities;

/// <summary>
/// A one-time pass to set a new password (S10), minted by an admin from
/// <c>POST /admin/users/{id}/password-reset</c> and spent anonymously at
/// <c>POST /auth/password/reset</c>.
/// <para>
/// <b>What this replaces.</b> Before it, a user who had forgotten their password — or who had locked
/// themselves out and was refused even the right one — could only be helped by an operator opening
/// <c>psql</c> against the volume and writing a hash by hand. That was tolerable while the only user
/// <i>was</i> the operator. It is not once other people have accounts.
/// </para>
/// <para>
/// <b>The admin never learns the password.</b> They hand over a one-time door and the user walks through
/// it, choosing their own credential on the far side. An endpoint that let an admin <i>set</i> a
/// password would hand them a working impersonation of any account on the box; this one does not.
/// </para>
/// <para>
/// <b>A separate table from <see cref="Invite"/>, not a shared one with a discriminator.</b> The two
/// differ in the ways that matter to a schema — an invite may be bound to an address and creates a user,
/// a reset belongs to one and changes it; their expiry policies differ by two orders of magnitude — so a
/// shared table would be half-null on every row. What <i>is</i> shared is the crypto, which lives once
/// in <c>SingleUseToken</c>: see there for why the fingerprint is SHA-256 rather than a password hash.
/// </para>
/// <para>
/// <b>The two foreign keys behave differently, deliberately.</b>
/// <list type="bullet">
/// <item><description>
/// <see cref="UserId"/> <b>cascades</b>. A reset is meaningless once the account it resets is gone —
/// there is nothing left to unlock and no audit value in "somebody who no longer exists was once sent a
/// token". Cascading also means the account-deletion pipeline in <c>AccountDeletion</c> needs no change:
/// Postgres clears these rows itself.
/// </description></item>
/// <item><description>
/// <see cref="CreatedByUserId"/> is nullable and <b>sets null</b>, the same as <see cref="Invite"/>'s.
/// Who issued a reset is worth keeping after that admin's own account is deleted; cascading would
/// destroy the record of an intervention on an account that still exists.
/// </description></item>
/// </list>
/// </para>
/// </summary>
public sealed class PasswordReset
{
    public int Id { get; set; }

    /// <summary>Base64url SHA-256 of the raw token. Unique — it is the lookup key.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The account this resets. Not nullable: a reset without one is not a reset.</summary>
    public int UserId { get; set; }

    /// <summary>The admin who issued it, or null once that account has been deleted.</summary>
    public int? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this stops working. The default window is <c>Auth:PasswordResetExpiryHours</c> — <b>hours,
    /// where an invite gets days</b>, and that difference is the point: an invite is an arrangement made
    /// in advance, while a reset is handed over during a conversation that is happening now. A reset
    /// still live a fortnight later is a credential nobody is watching.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// When it was spent, or null while it is still live.
    /// <para>
    /// Stamped rather than deleted, like <see cref="Invite"/>: it makes a replay a clean refusal instead
    /// of an indistinguishable "no such token", and it leaves an operator able to see that the reset
    /// they issued was actually taken up.
    /// </para>
    /// </summary>
    public DateTime? RedeemedAtUtc { get; set; }

    /// <summary>Whether this can still be spent, as of <paramref name="asOfUtc"/>.</summary>
    public bool IsRedeemable(DateTime asOfUtc) => RedeemedAtUtc is null && ExpiresAtUtc > asOfUtc;
}
