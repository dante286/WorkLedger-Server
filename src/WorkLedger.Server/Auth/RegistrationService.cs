using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Auth;

/// <summary>
/// Creating an account (<see cref="RegisterAsync"/>) and attaching credentials to the pre-S6 legacy row
/// (<see cref="ClaimAsync"/>). Both take the same input and validate it through <see cref="Credentials"/>.
/// <para>
/// <b>The secret's job moved, twice.</b> In S2 the bootstrap secret gated device bootstrap and was
/// therefore a token-issuing credential; from S6 it gates signup; from S7 it can no longer mint a token
/// at all — only a password can. That is a strict security improvement, and the configuration key is
/// deliberately unchanged so an existing deployment keeps its posture and its operator changes nothing.
/// From S10 an invite is the other way in, and <c>Auth:SignupSecretEnabled</c> decides whether the
/// secret still is.
/// </para>
/// <para>
/// <b>Neither mints anything.</b> Tokens are device-scoped and come from <see cref="CredentialCheck"/>
/// plus a device; registering is a user-level act with no device to bind one to.
/// </para>
/// </summary>
internal sealed class RegistrationService(
    WorkLedgerDbContext db,
    AuthOptions auth,
    BootstrapOptions bootstrap,
    PasswordService passwords,
    InviteService invites)
{
    /// <summary>
    /// Creates a user. Its gates run in a fixed order, each with its own distinct refusal, because an
    /// operator debugging a rejected signup needs to know which rule stopped it: registration disabled,
    /// no acceptable credential, the invite's own state, bad input, the invite's bound address, and a
    /// duplicate email.
    /// <para>
    /// <b>Two accepted credentials, not two modes:</b> <paramref name="providedSecret"/> (only while
    /// <c>Auth:SignupSecretEnabled</c>) or <paramref name="inviteToken"/>. Either satisfies registration,
    /// and <c>/auth/capabilities</c> advertises which ones this server will take.
    /// </para>
    /// <para>
    /// <b>An unknown invite token is <see cref="RegistrationRefusal.Unauthorized"/></b>, the same answer as
    /// bringing no credential at all: it is indistinguishable from a typo or a guess, and registration is
    /// reachable by anyone. The invite-specific refusals are different — they mean "this really is an
    /// invite, and here is what is wrong with it", which the holder needs in order to go back to whoever
    /// sent it. That includes a wrong address: the caller holds a one-time token addressed to somebody, so
    /// there is nothing left to hide, and a vague refusal would send them in circles.
    /// </para>
    /// <para>
    /// <b>The duplicate-email refusal does enumerate accounts</b>, which is an accepted tradeoff behind a
    /// shared secret or an invite on a self-hosted box: the honest error is worth far more than the leak
    /// costs when the caller already had to hold one of those to get here. NOTE THE ASYMMETRY, AND DO NOT
    /// "FIX" IT. <see cref="CredentialCheck"/> answers a wrong email and a wrong password identically,
    /// because it is reachable with no secret at all. Registration being frank about a duplicate is not a
    /// precedent for sign-in being frank about anything.
    /// </para>
    /// <para>
    /// <b>The invite is redeemed in the same transaction as the insert</b>, and locked
    /// (<see cref="InviteService.FindForUpdateAsync"/>) before it is inspected. A registration that fails
    /// after that point — including one that loses the unique-email race — rolls back and leaves the
    /// invite exactly as it found it. Redemption stamps the row rather than deleting it: who invited whom
    /// is most of the reason the table exists.
    /// </para>
    /// <para>
    /// <b>The first account on an empty database is the administrator</b> (S10). Deterministic, needs no
    /// seed and no configuration, and follows from what this server already promises: nothing is seeded,
    /// and the first registration creates the first user — who on a fresh box is by definition the person
    /// setting it up. A deployment that upgraded into S10 with users already in it gets its first admin
    /// from <see cref="AdminOperations.BootstrapAsync"/> instead. Two genuinely simultaneous first
    /// registrations can both read zero and both come out admin. That is harmless on a brand-new box and
    /// is deliberately not guarded: a lock on every signup to shave a race nobody is running would cost
    /// more than the outcome does.
    /// </para>
    /// </summary>
    public async Task<RegistrationResult> RegisterAsync(
        RegisterUserRequest? request, string? providedSecret, CancellationToken cancellationToken)
    {
        if (!auth.RegistrationEnabled)
        {
            return new RegistrationResult.Refused(RegistrationRefusal.Disabled);
        }

        var secretAccepted = auth.SignupSecretEnabled && bootstrap.SecretMatches(providedSecret);
        var inviteToken = request?.InviteToken?.Trim();

        if (!secretAccepted && string.IsNullOrEmpty(inviteToken))
        {
            return new RegistrationResult.Refused(RegistrationRefusal.Unauthorized);
        }

        // Opened here rather than around the insert alone, because the invite has to be locked before it
        // is inspected and stay locked until it is spent.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        Invite? invite = null;
        if (!secretAccepted)
        {
            invite = await invites.FindForUpdateAsync(inviteToken!, cancellationToken);
            if (invite is null)
            {
                return new RegistrationResult.Refused(RegistrationRefusal.Unauthorized);
            }

            if (invite.RedeemedAtUtc is not null)
            {
                return new RegistrationResult.Refused(RegistrationRefusal.InviteUsed);
            }

            if (invite.ExpiresAtUtc <= DateTime.UtcNow)
            {
                return new RegistrationResult.Refused(RegistrationRefusal.InviteExpired);
            }
        }

        if (!Credentials.TryValidate(request, auth, out var credentials, out var validationError))
        {
            return new RegistrationResult.Invalid(validationError);
        }

        // Checked after validation because it needs the normalized address.
        if (invite?.NormalizedEmail is { } boundTo &&
            !string.Equals(boundTo, credentials.NormalizedEmail, StringComparison.Ordinal))
        {
            return new RegistrationResult.Refused(RegistrationRefusal.InviteEmailMismatch);
        }

        if (await db.Users.AnyAsync(u => u.NormalizedEmail == credentials.NormalizedEmail, cancellationToken))
        {
            return new RegistrationResult.Refused(RegistrationRefusal.DuplicateEmail);
        }

        var isFirstUser = !await db.Users.AnyAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var user = new User
        {
            Email = credentials.Email,
            NormalizedEmail = credentials.NormalizedEmail,
            PasswordHash = passwords.Hash(credentials.Password),
            DisplayName = credentials.DisplayName,
            IsAdmin = isFirstUser,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two simultaneous signups for one address both clear the AnyAsync check above; the partial
            // unique index on NormalizedEmail is what actually settles it. Report the loser as the
            // conflict it is rather than as a 500. The transaction rolls back on dispose, so the invite
            // is not burned.
            return new RegistrationResult.Refused(RegistrationRefusal.DuplicateEmail);
        }

        if (invite is not null)
        {
            invite.RedeemedAtUtc = now;
            invite.RedeemedByUserId = user.Id;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new RegistrationResult.Succeeded(user);
    }

    /// <summary>
    /// The upgrade path for a deployment that already has the S2-seeded account and real data: attaches
    /// email and password to <b>that row</b>, in place, so every project, task, time entry and enrolled
    /// device carries over untouched. <see cref="RegistrationRefusal.NothingToClaim"/> when there is
    /// nothing claimable — see <see cref="UnclaimedUser"/> for what that means.
    /// <para>
    /// <b>Always requires the secret, and is not subject to <c>Auth:RegistrationEnabled</c> or
    /// <c>Auth:SignupSecretEnabled</c>.</b> An operator who switched registration or the signup secret
    /// off must still be able to claim their own existing data; the flags answer different questions.
    /// </para>
    /// <para>
    /// <b>SecurityStamp is not bumped here.</b> The stamp revokes outstanding tokens, and attaching a
    /// first password is not a credential rotation. (Those pre-S7 tokens carry no <c>stamp</c> claim and
    /// are refused on their own account — see <see cref="TokenRevocation"/>. What claiming preserves is
    /// the row, its data and its enrolled devices; every device then signs in once.)
    /// </para>
    /// </summary>
    public async Task<RegistrationResult> ClaimAsync(
        RegisterUserRequest? request, string? providedSecret, CancellationToken cancellationToken)
    {
        if (!bootstrap.SecretMatches(providedSecret))
        {
            return new RegistrationResult.Refused(RegistrationRefusal.Unauthorized);
        }

        if (!Credentials.TryValidate(request, auth, out var credentials, out var validationError))
        {
            return new RegistrationResult.Invalid(validationError);
        }

        var user = await UnclaimedUser.FindAsync(db, cancellationToken);
        if (user is null)
        {
            return new RegistrationResult.Refused(RegistrationRefusal.NothingToClaim);
        }

        user.Email = credentials.Email;
        user.NormalizedEmail = credentials.NormalizedEmail;
        user.PasswordHash = passwords.Hash(credentials.Password);
        user.DisplayName = credentials.DisplayName ?? user.DisplayName;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return new RegistrationResult.Succeeded(user);
    }

    /// <summary>
    /// The only unique index on <c>Users</c> is the partial one on <c>NormalizedEmail</c>, so a unique
    /// violation from a user insert can only be a duplicate email. Matching on the SqlState rather than
    /// on the message keeps this independent of Postgres's error wording and of the locale it renders in.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

/// <summary>What <see cref="RegistrationService"/> did.</summary>
internal abstract record RegistrationResult
{
    /// <summary>The account was created (register) or credentialed (claim).</summary>
    public sealed record Succeeded(User User) : RegistrationResult;

    /// <summary>The input fails <see cref="Credentials"/>; <see cref="Error"/> says how.</summary>
    public sealed record Invalid(string Error) : RegistrationResult;

    /// <summary>A gate refused it. See <see cref="RegistrationService.RegisterAsync"/> for the order.</summary>
    public sealed record Refused(RegistrationRefusal Reason) : RegistrationResult;
}

internal enum RegistrationRefusal
{
    /// <summary><c>Auth:RegistrationEnabled</c> is off.</summary>
    Disabled,

    /// <summary>No acceptable secret and no invite token, or an invite token that matches nothing.</summary>
    Unauthorized,

    InviteUsed,
    InviteExpired,
    InviteEmailMismatch,
    DuplicateEmail,

    /// <summary>Claim only: there is no unclaimed legacy row.</summary>
    NothingToClaim,
}
