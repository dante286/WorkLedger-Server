using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Auth;

/// <summary>
/// What a signed-in person can do to their own account (S8): list the devices attached to it, detach
/// one, change the password, sign every device out, or delete the lot.
/// <para>
/// <b>Every method is scoped by a <c>userId</c> the caller took from the authenticated principal</b>,
/// never from a body or a URL — the tenancy invariant in <c>CLAUDE.md</c> — so the worst a crafted
/// request can do is address something of its own. <see cref="AdminOperations"/> is the only code that
/// acts on another user, and it is a different class behind a different policy.
/// </para>
/// <para>
/// <b>Revocation is evicted, not waited out.</b> Everything here that kills tokens drops the affected
/// <see cref="TokenRevocation"/> cache entries <i>after</i> its write commits, so the kill lands on the
/// very next request instead of up to <c>Auth:StampCacheSeconds</c> later. Evicting before the commit
/// would only let a request already in flight re-cache the value being replaced.
/// </para>
/// <para>
/// <b>Nothing here mints a token.</b> The two stamp-bumping methods return the updated user so a caller
/// that is itself a device can re-mint for itself — otherwise changing your password signs you out of the
/// app you just changed it in, which reads as a bug.
/// </para>
/// </summary>
public sealed class AccountSelfService(
    WorkLedgerDbContext db,
    AuthOptions auth,
    PasswordService passwords,
    IMemoryCache cache,
    AccountDeletion accountDeletion)
{
    /// <summary>
    /// The devices signed in to <paramref name="userId"/>, most recently seen first.
    /// <paramref name="currentDeviceId"/> is the calling install, marked <c>IsCurrent</c>; null when the
    /// caller is not a device, and then nothing is. Another user's devices are not merely hidden but
    /// unreachable, since the query never sees a caller-supplied user id.
    /// <para>
    /// <b>Ordering puts never-seen devices last, not first.</b> Postgres sorts nulls first under
    /// <c>DESC</c>, which would float a row that has never made a request above the device the user is
    /// looking at the list from. <c>LastSeenUtc</c> is written on every login and so is null only for a
    /// row created some other way, but the fix costs one term and the alternative reads as a bug.
    /// </para>
    /// </summary>
    public Task<List<DeviceSummary>> ListDevicesAsync(
        int userId, string? currentDeviceId, CancellationToken cancellationToken) =>
        db.Devices
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.LastSeenUtc.HasValue)
            .ThenByDescending(d => d.LastSeenUtc)
            // A stable tiebreak so two devices seen in the same millisecond do not swap places between
            // calls and make the list flicker.
            .ThenBy(d => d.Id)
            .Select(d => new DeviceSummary(
                d.DeviceId, d.Label, d.CreatedAt, d.LastSeenUtc, d.DeviceId == currentDeviceId))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Detaches one device from <paramref name="userId"/>. False for a <paramref name="deviceId"/> that
    /// is unknown <i>or belongs to another user</i> — deliberately the same answer, because a
    /// distinguishable "exists but not yours" would confirm another account's device id to anyone willing
    /// to guess.
    /// <para>
    /// <b>The device's synced rows are deliberately left alone.</b> This is the single most plausible
    /// wrong assumption about this operation, so: projects, tasks and time entries belong to the
    /// <i>user</i>, not to the device that happened to create them. <c>OriginDeviceId</c> on those rows
    /// is provenance — it tells a pull which changes to echo back — not ownership. Deleting the rows a
    /// retired laptop originated would destroy history every other device is still syncing. Removing a
    /// device revokes its access and nothing else.
    /// </para>
    /// <para>
    /// <b>Removing the caller's own current device is allowed, and means "sign this device out".</b> It
    /// is not a mistake to be guarded against: it is the only way to drop a token from the device holding
    /// it. The device re-enrolls by signing in again — the row is recreated under the same
    /// <c>(UserId, DeviceId)</c>, and finds the account's data exactly as it left it.
    /// </para>
    /// <para>
    /// Revocation itself needs no code: <see cref="TokenRevocation"/> already fails any token whose
    /// device row is gone. All this adds is the cache eviction that makes it immediate.
    /// </para>
    /// </summary>
    public async Task<bool> RemoveDeviceAsync(int userId, string deviceId, CancellationToken cancellationToken)
    {
        var device = await db.Devices.SingleOrDefaultAsync(
            d => d.UserId == userId && d.DeviceId == deviceId, cancellationToken);
        if (device is null)
        {
            return false;
        }

        var deviceRowId = device.Id;
        db.Devices.Remove(device);
        await db.SaveChangesAsync(cancellationToken);

        // After the commit. The cached snapshot is keyed by the row id, and dropping it turns the next
        // request from this device into a fresh lookup that finds nothing — a 401.
        cache.Remove(TokenRevocation.CacheKey(deviceRowId));
        return true;
    }

    /// <summary>
    /// Rotates <paramref name="userId"/>'s password. <b>The current password is re-verified even though
    /// the caller is already signed in</b>: a stolen session must not be enough to take the account over.
    /// The new password goes through the same policy registration applies, from the same accessor, so the
    /// two cannot drift.
    /// <para>
    /// Bumping <c>SecurityStamp</c> is what makes this a real rotation: every outstanding token dies, on
    /// every device. The lockout counters are cleared with it. The cache entries are evicted directly
    /// rather than left to expire, so the sign-out is immediate rather than lagging
    /// <c>Auth:StampCacheSeconds</c> behind.
    /// </para>
    /// <para>
    /// The unclaimed legacy row (a null <c>PasswordHash</c>) is <see cref="SelfServiceOutcome.NoPassword"/>
    /// rather than <see cref="SelfServiceOutcome.WrongPassword"/>: there is no current password to
    /// mismatch, the caller is already authenticated so there is nothing to hide, and "wrong password"
    /// would send an operator hunting for a password that never existed.
    /// </para>
    /// </summary>
    public async Task<SelfServiceResult> ChangePasswordAsync(
        int userId, string? currentPassword, string? newPassword, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return new SelfServiceResult(SelfServiceOutcome.NoSuchUser);
        }

        if (user.PasswordHash is null)
        {
            return new SelfServiceResult(SelfServiceOutcome.NoPassword);
        }

        if (passwords.Verify(user.PasswordHash, currentPassword ?? string.Empty) is PasswordVerification.Failed)
        {
            return new SelfServiceResult(SelfServiceOutcome.WrongPassword);
        }

        if (!Credentials.TryValidatePassword(newPassword, auth, out var validationError))
        {
            return new SelfServiceResult(SelfServiceOutcome.InvalidPassword, Error: validationError);
        }

        user.PasswordHash = passwords.Hash(newPassword ?? string.Empty);
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await TokenRevocation.EvictUserAsync(db, cache, user.Id, cancellationToken);

        return new SelfServiceResult(SelfServiceOutcome.Done, user);
    }

    /// <summary>
    /// Signs every device out at once by rotating <paramref name="userId"/>'s <c>SecurityStamp</c>, which
    /// is what every outstanding token carries a copy of and is checked against on every request. The
    /// device rows survive — <b>only tokens die</b>, and each device gets back in by signing in again.
    /// <see cref="RemoveDeviceAsync"/> is the one that actually detaches a device.
    /// <para>
    /// <b>No re-authentication.</b> Unlike a password change or an account deletion, the worst outcome
    /// here is that everyone signs in again — the action is annoying, not destructive, and demanding a
    /// password from someone who suspects a stolen token would slow down the one response that helps.
    /// </para>
    /// </summary>
    public async Task<SelfServiceResult> SignOutEverywhereAsync(int userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return new SelfServiceResult(SelfServiceOutcome.NoSuchUser);
        }

        user.SecurityStamp = Guid.NewGuid().ToString();
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await TokenRevocation.EvictUserAsync(db, cache, user.Id, cancellationToken);

        return new SelfServiceResult(SelfServiceOutcome.Done, user);
    }

    /// <summary>
    /// Deletes <paramref name="userId"/>'s own account and everything under it through
    /// <see cref="AccountDeletion"/>, whose doc covers what that means. On a self-hosted box the person
    /// doing this <i>is</i> the operator, so no backup covers the mistake — which is why there are two
    /// independent gates before anything is dropped:
    /// <list type="number">
    /// <item><description>
    /// <b>The current password is re-verified</b> (<see cref="SelfServiceOutcome.WrongPassword"/>). A
    /// leaked token must not be sufficient to destroy an account.
    /// </description></item>
    /// <item><description>
    /// <b><paramref name="confirmEmail"/> must match this account's address</b>, case-insensitively
    /// (<see cref="SelfServiceOutcome.ConfirmationMismatch"/>). That is the typed-confirmation gesture,
    /// and it independently stops a client bug pointed at the wrong session from deleting the wrong
    /// account.
    /// </description></item>
    /// </list>
    /// Either failing leaves the account completely intact; nothing is written before both have passed.
    /// A credential-less pre-S6 row (see <see cref="User"/>) has no password to verify and is
    /// <see cref="SelfServiceOutcome.NoPassword"/> — refusing in the safe direction, and in practice
    /// unreachable since such a row cannot sign in.
    /// <para>
    /// <b>There is deliberately no last-administrator guard here</b>, unlike on every
    /// <see cref="AdminOperations"/> method that could remove one. The sole admin may delete their own
    /// account and leave the server with none. Those two rules are not in conflict: the admin guards stop
    /// one admin from stripping <i>another</i>, or from stranding a deployment full of users it cannot
    /// administer, and they are reachable by someone who should be told to promote a successor first. This
    /// is a person deleting themselves, having passed both gates above, and refusing them would be telling
    /// someone they may not leave. The state it produces is recoverable by design and by exactly the
    /// operation built for it: with no admin left, <see cref="AdminOperations.BootstrapAsync"/> starts
    /// answering again, so the next person with the secret can promote themselves. <b>Do not "fix" this
    /// with a guard</b> — it would make the last account on a server undeletable.
    /// </para>
    /// </summary>
    public async Task<SelfServiceOutcome> DeleteAccountAsync(
        int userId, string? password, string? confirmEmail, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return SelfServiceOutcome.NoSuchUser;
        }

        if (user.PasswordHash is null)
        {
            return SelfServiceOutcome.NoPassword;
        }

        if (passwords.Verify(user.PasswordHash, password ?? string.Empty) is PasswordVerification.Failed)
        {
            return SelfServiceOutcome.WrongPassword;
        }

        if (!AccountDeletion.ConfirmationMatches(user, confirmEmail))
        {
            return SelfServiceOutcome.ConfirmationMismatch;
        }

        await accountDeletion.DeleteUserAsync(userId, actorUserId: null, cancellationToken);
        return SelfServiceOutcome.Done;
    }
}

/// <summary>
/// What an <see cref="AccountSelfService"/> method did. <see cref="User"/> is set on
/// <see cref="SelfServiceOutcome.Done"/> from the stamp-bumping methods, carrying the new stamp;
/// <see cref="Error"/> on <see cref="SelfServiceOutcome.InvalidPassword"/>.
/// </summary>
public sealed record SelfServiceResult(SelfServiceOutcome Outcome, User? User = null, string? Error = null);

public enum SelfServiceOutcome
{
    Done,

    /// <summary>The caller's own row is gone — all but unreachable behind revocation.</summary>
    NoSuchUser,

    /// <summary>The credential-less pre-S6 row, which has no password to verify.</summary>
    NoPassword,

    /// <summary>The re-verified current password is wrong.</summary>
    WrongPassword,

    /// <summary>The new password fails policy.</summary>
    InvalidPassword,

    /// <summary>The typed confirmation is blank or is not this account's email.</summary>
    ConfirmationMismatch,
}
