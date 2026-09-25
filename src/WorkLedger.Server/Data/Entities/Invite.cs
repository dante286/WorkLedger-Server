namespace WorkLedger.Server.Data.Entities;

/// <summary>
/// A one-time pass to create an account (S10), issued by an admin from <c>POST /admin/invites</c> and
/// spent at <c>POST /auth/register</c>.
/// <para>
/// <b>What this replaces.</b> Before it, onboarding somebody meant handing them <c>Bootstrap:Secret</c>
/// — the operator's own credential, the same string that gates <c>/auth/claim</c> and sits in
/// <c>.env</c>. It could not be scoped to one person, given an expiry, taken back, or reconciled
/// afterwards. An invite is all four.
/// </para>
/// <para>
/// <b><see cref="TokenHash"/> is SHA-256, not a password hash.</b> The reasoning lives on
/// <c>SingleUseToken</c>; the short version is that a 256-bit random token has nothing to slow a guesser
/// down over, and a salted hash could not carry the unique index this is looked up by. The raw token
/// exists in exactly one response and is never recoverable from here.
/// </para>
/// <para>
/// <b>Redeeming stamps the row rather than deleting it.</b> Who invited whom, and when it was taken up,
/// is most of why this table is worth having — a deleted row answers none of it. It also makes a replay
/// a clean <c>409</c> instead of an indistinguishable "no such invite".
/// </para>
/// <para>
/// <b>Both user references are nullable, with <c>ON DELETE SET NULL</c>, and that is deliberate.</b> An
/// invite outlives the accounts at either end of it: deleting the admin who issued one must not silently
/// destroy the record of an account that still exists, and deleting the invited user must not either.
/// Cascading would do exactly that, and adding this table to the account-deletion pipeline in
/// <c>AccountDeletion</c> would mean the same loss by hand. So that pipeline needs no change at all — the constraint does the right thing on its own, and a null here reads as
/// "that account is gone", not as data corruption.
/// </para>
/// </summary>
public sealed class Invite
{
    public int Id { get; set; }

    /// <summary>Base64url SHA-256 of the raw token. Unique — it is the lookup key.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// The address this invite is bound to, upper-cased the same way <see cref="User.NormalizedEmail"/>
    /// is, or <b>null for an open invite</b> that anyone may redeem once. Binding is a guard against a
    /// forwarded token being used by the wrong person; it is not authentication, since whoever holds the
    /// token still chooses their own password.
    /// </summary>
    public string? NormalizedEmail { get; set; }

    /// <summary>The admin who issued it, or null once that account has been deleted.</summary>
    public int? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this stops being redeemable. Always set — there is no never-expiring invite, because the
    /// whole point is that a leaked one stops working by itself. The default window is
    /// <c>Auth:InviteExpiryDays</c>; the issuing admin may shorten or lengthen it per invite.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>When it was spent, or null while it is still live.</summary>
    public DateTime? RedeemedAtUtc { get; set; }

    /// <summary>The account it created, or null if unredeemed or if that account has since been deleted.</summary>
    public int? RedeemedByUserId { get; set; }

    /// <summary>Free-form operator note — "contractor, Q3" — so a list of invites is readable months later.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Whether this can still be spent, as of <paramref name="asOfUtc"/>. Both halves matter and are
    /// reported separately to the caller, because "you already used this" and "this ran out" send a
    /// person to two different next steps.
    /// </summary>
    public bool IsRedeemable(DateTime asOfUtc) => RedeemedAtUtc is null && ExpiresAtUtc > asOfUtc;
}
