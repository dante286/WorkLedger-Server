using Microsoft.EntityFrameworkCore;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Auth;

/// <summary>
/// Verifies an email and password, and owns the per-user failure budget that comes with doing so.
/// <b>It mints nothing</b>: <c>POST /auth/login</c> is "check, then upsert the device and mint a token",
/// and any other way of signing in is "check, then do its own thing". Every caller therefore spends the
/// same budget, which is correct — it is one account and one password, however it is being presented.
/// <para>
/// <b>Every failure is the same <see cref="CredentialCheckResult.Invalid"/>, deliberately.</b> Unknown
/// email, wrong password and a credential-less unclaimed row (see <see cref="User"/>) are
/// indistinguishable. A sign-in form is reachable by anyone who has the URL, so a distinguishable "no such
/// account" would make it an account-enumeration oracle. It reads as inconsistent next to registration's
/// honest duplicate-email refusal — that asymmetry is intentional and is explained where
/// <see cref="RegistrationService"/> raises it: registration sits behind the operator's shared secret or
/// an invite, and this does not.
/// </para>
/// <para>
/// <b>A suspended account (S10) answers the same <see cref="CredentialCheckResult.Invalid"/>.</b> Saying
/// "this account is suspended" would tell an anonymous caller that the address exists, which is exactly
/// what the uniform failure exists to prevent — the admin who suspended it can see the state, and the
/// person affected has to ask them either way.
/// </para>
/// <para>
/// <b>The three lockout counter rules.</b> The first is obvious; the other two are what a naive
/// implementation gets wrong.
/// <list type="number">
/// <item><description>
/// <b>Counters reset on success.</b> A successful check zeroes <c>FailedLoginCount</c> and clears
/// <c>LockoutEndUtc</c>.
/// </description></item>
/// <item><description>
/// <b>The counter also resets when the lockout window expires</b>, before the attempt is evaluated.
/// Without this, a successful login is the only thing that ever clears the count, so after the first
/// lockout the count stays at the threshold forever: the window lifts, the user's next single typo is
/// compared against a counter already at the limit, and the account re-locks immediately. It is then
/// permanently on one-typo-per-<c>Auth:LockoutMinutes</c>, escapable only by a successful login —
/// exactly what a user who has forgotten which password they used cannot produce.
/// </description></item>
/// <item><description>
/// <b>A <see cref="CredentialCheckResult.LockedOut"/> answer for an account already locked writes nothing
/// at all</b> — no increment, no re-stamp. The naive <c>LockoutEndUtc = now + LockoutMinutes</c> on every
/// refusal lets anything that polls a sign-in form hold the account locked <i>indefinitely</i>: a buggy
/// client, a script, or anyone who knows the email address. On a self-hosted box the locked-out user
/// <i>is</i> the operator, the lockout refuses their correct password, and the only recovery is an admin
/// or a <c>psql</c> <c>UPDATE</c> against the volume. The window is computed once, at the moment the
/// threshold is crossed, and only read thereafter.
/// </description></item>
/// </list>
/// Known tradeoff even with all three in place: on a single-user instance, someone who knows the
/// email can keep the owner locked out <c>Auth:LockoutMinutes</c> at a time by burning the counter
/// down again. That is accepted for a self-hosted app — the alternative, no lockout at all, is worse
/// — and rule 3 is what keeps it <i>at a time</i> rather than permanent. S10 only <i>partly</i> closed
/// it: an admin can now lift a lockout (<see cref="AdminOperations.UnlockAsync"/>) and the per-IP rate
/// limit slows whoever is burning the counter, but the counter is still per account. Counting failures
/// per (email, source) is what would remove it, and is a separate change.
/// </para>
/// <para>
/// The lockout and the suspension are both checked <b>before</b> the hash is verified, so a locked or
/// suspended account burns no PBKDF2 cycles, and the correct password is refused for as long as the
/// window lasts. Neither writes, so neither can move the failure counter.
/// </para>
/// <para>
/// <b>A successful check commits its own writes</b> — the zeroed counters, <c>LastLoginUtc</c>, and a
/// rehash when the stored hash is on an older iteration count — before returning, so no caller can
/// verify a password and forget to reset the budget it spent.
/// </para>
/// </summary>
public sealed class CredentialCheck(WorkLedgerDbContext db, AuthOptions auth, PasswordService passwords)
{
    public async Task<CredentialCheckResult> CheckAsync(
        string? email, string? password, CancellationToken cancellationToken)
    {
        var normalizedEmail = (email?.Trim() ?? string.Empty).ToUpperInvariant();
        var user = normalizedEmail.Length == 0
            ? null
            : await db.Users.SingleOrDefaultAsync(
                u => u.NormalizedEmail == normalizedEmail, cancellationToken);

        // One condition, two very different causes — no such user, and an unclaimed row with no
        // password to check — and deliberately the same answer for both.
        if (user?.PasswordHash is null)
        {
            return CredentialCheckResult.Invalid.Instance;
        }

        if (user.DisabledAtUtc is not null)
        {
            return CredentialCheckResult.Invalid.Instance;
        }

        var now = DateTime.UtcNow;

        // Rule 2: an expired window is cleared before the attempt is evaluated, never after.
        if (user.LockoutEndUtc is { } expired && expired <= now)
        {
            user.FailedLoginCount = 0;
            user.LockoutEndUtc = null;
        }

        // Rule 3: a pure read of state. Reached only when rule 2 did not clear the window, and it
        // writes nothing — not the counter, not the window.
        if (auth.LockoutEnabled && user.LockoutEndUtc is { } lockedUntil)
        {
            return new CredentialCheckResult.LockedOut(lockedUntil);
        }

        var verification = passwords.Verify(user.PasswordHash, password ?? string.Empty);
        if (verification is PasswordVerification.Failed)
        {
            user.FailedLoginCount++;
            if (auth.LockoutEnabled && user.FailedLoginCount >= auth.MaxFailedLogins)
            {
                user.LockoutEndUtc = now + auth.LockoutWindow;
            }

            // Gated on the option rather than read off the row: a window stamped before an operator set
            // Auth:MaxFailedLogins to 0 is still on the row until it expires, and "0 disables the lockout"
            // has to hold from the next request, not from whenever that window happens to end.
            var justLocked = auth.LockoutEnabled ? user.LockoutEndUtc : null;
            user.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);

            // The attempt that crosses the threshold answers LockedOut rather than Invalid: the caller's
            // next question is "when can I try again", and this is the answer to it.
            return justLocked is { } until
                ? new CredentialCheckResult.LockedOut(until)
                : CredentialCheckResult.Invalid.Instance;
        }

        // Rehash on the way through. The whole point of a versioned hasher is that an
        // iteration-count bump is detectable rather than a silent lockout years from now, and acting
        // on it costs one column on a sign-in that was already writing.
        if (verification is PasswordVerification.RehashNeeded)
        {
            user.PasswordHash = passwords.Hash(password ?? string.Empty);
        }

        // Rule 1.
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginUtc = now;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        return new CredentialCheckResult.Verified(user);
    }
}

/// <summary>What <see cref="CredentialCheck"/> concluded. See it for why there is only one kind of failure.</summary>
public abstract record CredentialCheckResult
{
    /// <summary>The password is right. <see cref="User"/> is tracked, with the success writes already saved.</summary>
    public sealed record Verified(User User) : CredentialCheckResult;

    /// <summary>Unknown email, wrong password, credential-less row or suspended account — on purpose, one answer.</summary>
    public sealed record Invalid : CredentialCheckResult
    {
        public static readonly Invalid Instance = new();
    }

    /// <summary>The account is locked until <see cref="UntilUtc"/>, either already or as of this attempt.</summary>
    public sealed record LockedOut(DateTime UntilUtc) : CredentialCheckResult;
}
