using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Auth;

/// <summary>
/// Issuing and redeeming password resets (S10): a single-use token that lets one user set a new password,
/// minted by an admin and spent by whoever the admin handed it to.
/// <para>
/// <b>Issuing does not set a password.</b> Letting an admin choose someone's credential would hand them
/// a working impersonation of any account on the box; this hands them a door and nothing else. Who may
/// issue, and to whom, is <see cref="AdminOperations.IssuePasswordResetAsync"/>'s decision.
/// </para>
/// <para>
/// <b>No email is sent</b> — this server has no SMTP, and gaining one is a dependency and a decision,
/// not a detail. The admin passes the token on however they are already talking to the person.
/// Self-service "forgot my password" is the follow-up that needs mail.
/// </para>
/// </summary>
internal sealed class PasswordResetService(
    WorkLedgerDbContext db,
    AuthOptions auth,
    PasswordService passwords,
    IMemoryCache cache)
{
    /// <summary>
    /// Mints a reset for <paramref name="userId"/> and returns the raw token, which the caller must hand
    /// back exactly once — only its <see cref="SingleUseToken.Hash"/> is stored.
    /// <para>
    /// <b>Issuing a reset invalidates that user's outstanding ones</b>, in the same transaction.
    /// Otherwise an admin who reissues because "the first one did not arrive" leaves two live doors into
    /// the same account, and the one that went astray is the one nobody is watching. There is at most one
    /// live reset per user at any moment, by construction.
    /// </para>
    /// </summary>
    public async Task<IssuedPasswordReset> IssueAsync(
        int userId, int createdByUserId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.PasswordResets
            .Where(r => r.UserId == userId && r.RedeemedAtUtc == null)
            .ExecuteDeleteAsync(cancellationToken);

        var token = SingleUseToken.Generate();
        var now = DateTime.UtcNow;
        var reset = new PasswordReset
        {
            TokenHash = SingleUseToken.Hash(token),
            UserId = userId,
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            ExpiresAtUtc = now.AddHours(auth.PasswordResetExpiryHours),
        };
        db.PasswordResets.Add(reset);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new IssuedPasswordReset(token, reset.ExpiresAtUtc);
    }

    /// <summary>
    /// Spends <paramref name="token"/>: sets <paramref name="newPassword"/>, signs every device out, and
    /// clears the lockout.
    /// <para>
    /// <b>Unknown, expired and already-spent tokens are one identical
    /// <see cref="PasswordResetRedemption.InvalidToken"/>.</b> Distinguishing them would turn an anonymous
    /// endpoint into an oracle about which tokens have existed — the same reasoning as
    /// <see cref="CredentialCheck"/>'s uniform failure, and worth the slightly worse error message.
    /// </para>
    /// <para>
    /// <b>The password is validated before the token is looked up</b>, so a caller who has fat-fingered
    /// their new password is told so rather than being made to ask for another token, and the token is
    /// still spendable afterwards. The token is single-use, not single-attempt.
    /// </para>
    /// <para>
    /// <b>The stamp is bumped, and nothing is re-minted.</b> There is no device to mint for — redemption
    /// names none — and signing every device in again is the correct outcome for a credential the person
    /// had lost control of.
    /// </para>
    /// <para>
    /// <b>The lockout counters are cleared.</b> Somebody resetting a password has very often just locked
    /// themselves out guessing at the old one, and leaving the counter armed would refuse the password
    /// they set ten seconds earlier.
    /// </para>
    /// <para>
    /// <b>It does not un-suspend anybody.</b> Setting a credential and restoring access are different
    /// decisions, and only an admin makes the second one.
    /// </para>
    /// </summary>
    public async Task<PasswordResetRedemption> RedeemAsync(
        string? token, string? newPassword, CancellationToken cancellationToken)
    {
        token = token?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return PasswordResetRedemption.MissingToken.Instance;
        }

        if (!Credentials.TryValidatePassword(newPassword, auth, out var validationError))
        {
            return new PasswordResetRedemption.InvalidPassword(validationError);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var reset = await FindForUpdateAsync(token, cancellationToken);
        if (reset is null || !reset.IsRedeemable(DateTime.UtcNow))
        {
            return PasswordResetRedemption.InvalidToken.Instance;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == reset.UserId, cancellationToken);
        if (user is null)
        {
            // Unreachable while the FK cascades — the reset would have gone with the user. Answered
            // as the same flat refusal rather than a 500, because from the caller's side an account
            // that no longer exists and a token that never did are the same dead end.
            return PasswordResetRedemption.InvalidToken.Instance;
        }

        var now = DateTime.UtcNow;
        user.PasswordHash = passwords.Hash(newPassword ?? string.Empty);
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.UpdatedAt = now;
        reset.RedeemedAtUtc = now;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // After the commit, never before: evicting first would only let a request already in flight
        // re-cache the stamp this call just replaced.
        await TokenRevocation.EvictUserAsync(db, cache, user.Id, cancellationToken);

        return PasswordResetRedemption.Redeemed.Instance;
    }

    /// <summary>
    /// The reset matching <paramref name="token"/>, locked for the rest of the caller's transaction.
    /// Same pattern, and same reasoning, as <see cref="InviteService.FindForUpdateAsync"/>: without the
    /// lock two requests presenting one token both read it unredeemed and both succeed.
    /// </summary>
    private async Task<PasswordReset?> FindForUpdateAsync(string token, CancellationToken cancellationToken)
    {
        var hash = SingleUseToken.Hash(token);
        var resets = await db.PasswordResets
            .FromSql($"""SELECT * FROM "PasswordResets" WHERE "TokenHash" = {hash} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        return resets.SingleOrDefault();
    }
}

/// <summary>A freshly minted reset: the raw token, returned exactly once, and when it stops working.</summary>
internal sealed record IssuedPasswordReset(string Token, DateTime ExpiresAtUtc);

/// <summary>What <see cref="PasswordResetService.RedeemAsync"/> did.</summary>
internal abstract record PasswordResetRedemption
{
    public sealed record Redeemed : PasswordResetRedemption
    {
        public static readonly Redeemed Instance = new();
    }

    /// <summary>No token was presented at all.</summary>
    public sealed record MissingToken : PasswordResetRedemption
    {
        public static readonly MissingToken Instance = new();
    }

    /// <summary>The new password fails policy. The token was not looked up, so it is still spendable.</summary>
    public sealed record InvalidPassword(string Error) : PasswordResetRedemption;

    /// <summary>Unknown, expired or already spent — deliberately one answer.</summary>
    public sealed record InvalidToken : PasswordResetRedemption
    {
        public static readonly InvalidToken Instance = new();
    }
}
