using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Email;

/// <summary>
/// "Your WorkLedger password was changed" (S12 item 17), sent <b>after the commit</b> by the two services that
/// change an existing password: <c>AccountSelfService.ChangePasswordAsync</c> and
/// <c>PasswordResetService.RedeemAsync</c>. Setting a first password (registration, claiming the pre-S6 row)
/// and the transparent rehash at sign-in change nothing the owner needs telling about, and send nothing.
/// <para>
/// <b>It fires whichever caller reached the service — including the JSON compatibility route
/// <c>POST /auth/password</c>.</b> Decided and signed off on 2026-11-03 (WL-213). S11 says no new behaviour
/// lands on compatibility routes, but <c>CLAUDE.md</c> says a rule goes on the service and never on one of its
/// callers, and this is the case that settles it: a password changed through that route by someone holding a
/// stolen device token is exactly the takeover this notice exists to catch. The route's status and body are
/// unchanged. Exempting it would take a caller flag on a service whose point is to have none.
/// </para>
/// <para>
/// <b>Best effort.</b> With email off nothing is queued; with the queue full the notice is dropped; a relay
/// that refuses it is the delivery worker's single log line. None of that ever fails the password change.
/// </para>
/// <para>
/// <b>It carries no link and no token</b> (<see cref="EmailMessages.PasswordChanged"/>), so nothing in it can
/// be acted on, and a forged copy gets an attacker nothing. Not sent for sign-out-everywhere, which changes no
/// credential, nor for account deletion, after which there is no account left to protect.
/// </para>
/// </summary>
public sealed class SecurityNotices(EmailOptions options, EmailOutbox outbox)
{
    public void PasswordChanged(User user, DateTime changedAtUtc)
    {
        if (!options.Enabled || string.IsNullOrEmpty(user.Email))
        {
            return;
        }

        // The result is ignored on purpose: a full queue drops the notice, and the change stands.
        outbox.TryEnqueue(new OutgoingEmail(
            EmailKind.PasswordChanged, user.Id, user.Email, EmailMessages.PasswordChanged(changedAtUtc)));
    }
}
