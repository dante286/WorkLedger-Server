using Microsoft.EntityFrameworkCore;
using WorkLedger.Server.Data;
using WorkLedger.Server.Email;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Auth;

/// <summary>
/// Self-service password recovery (S12): what a <c>/portal/forgot</c> request turns into. <b>It runs in
/// <see cref="EmailDeliveryService"/>, never in the request</b> — the page only queues a
/// <see cref="ForgotPasswordRequest"/> and answers identically for every address, so neither its content nor
/// its timing can say whether an account exists. Everything that does depend on the account happens here.
/// <para>
/// <b>Who gets a message:</b> a user matched on <c>NormalizedEmail</c> who has a password and <b>is not
/// suspended</b>. A suspended user gets nothing, because a reset does not lift a suspension (S10's
/// reset-then-re-enable order): the message would set a password they still cannot use, and suggest it had
/// worked. The unclaimed pre-S6 row has no address, so nothing can match it.
/// </para>
/// <para>
/// <b>The address on file was never verified</b> (S12 Out of scope). Under the signup-secret path an account
/// can have been registered with somebody else's address, and that person can then take it over from here.
/// It harms nobody — whoever registered the address had already given it away — and it is recorded in the
/// README's security notes rather than built around.
/// </para>
/// <para>
/// <b>The cooldown (<c>Email:ResetCooldownMinutes</c>).</b> If a self-service reset was issued for this user
/// within the window, nothing is issued or sent. Two reasons: without it, the form would flood the inbox at
/// whatever rate the per-IP limit allows from each address an attacker controls; and, more subtly, every
/// issue invalidates the user's outstanding reset, so each repeat request would also cancel the link the
/// real user is about to click. <b>Only self-service issues count toward it, and only self-service requests
/// wait on it</b> — an admin-issued reset never does, though a self-service one still invalidates it.
/// </para>
/// </summary>
public sealed class SelfServiceRecovery(
    WorkLedgerDbContext db,
    PasswordResetService passwordResets,
    EmailOptions email,
    PortalLinks links,
    ILoggerFactory loggerFactory)
{
    /// <summary>
    /// Issues a self-service reset for the account at <paramref name="normalizedEmail"/> and returns its
    /// message, or returns null when that address gets nothing. Never says why to the requester — there is
    /// no requester by the time this runs.
    /// </summary>
    public async Task<OutgoingEmail?> PrepareAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .Select(u => new { u.Id, u.Email, u.PasswordHash, u.DisabledAtUtc })
            .SingleOrDefaultAsync(cancellationToken);

        var logger = loggerFactory.CreateLogger("AccountAction");
        if (user is null || user.Email is null || user.PasswordHash is null || user.DisabledAtUtc is not null)
        {
            // Debug, and no address: the operator gains nothing from a line per unknown address, and it
            // would turn the log into a list of who tried what.
            logger.LogDebug("A self-service password reset request matched no eligible account.");
            return null;
        }

        if (email.ResetCooldownMinutes > 0)
        {
            var since = DateTime.UtcNow - email.ResetCooldown;
            if (await db.PasswordResets.AnyAsync(
                    r => r.UserId == user.Id && r.SelfService && r.CreatedAt > since, cancellationToken))
            {
                logger.LogInformation(
                    "Self-service password reset for user {UserId} not issued: one was issued in the last {Minutes} minutes.",
                    user.Id, email.ResetCooldownMinutes);
                return null;
            }
        }

        var reset = await passwordResets.IssueAsync(user.Id, createdByUserId: null, selfService: true, cancellationToken);

        // Ids only, like every account audit line.
        logger.LogInformation("User {UserId}: self-service-reset-issued.", user.Id);

        return new OutgoingEmail(
            EmailKind.SelfServicePasswordReset,
            user.Id,
            user.Email,
            EmailMessages.SelfServicePasswordReset(links, reset.Token, reset.ExpiresAtUtc));
    }
}
