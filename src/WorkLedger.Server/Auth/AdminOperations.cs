using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Auth;

/// <summary>
/// Everything an admin can do to an account that is not their own (S10): see who exists, lift a lockout,
/// suspend and restore, hand out and take back admin, delete, and issue a password reset — plus the
/// one-shot promotion that creates the first admin on a deployment that has none. Each method returns an
/// <see cref="AdminOutcome"/>; the caller decides what that looks like (a status code, a page message).
/// <para>
/// <b>Authorization is the caller's job; the guards are this class's.</b> Nothing here checks that the
/// actor is an admin — the route or page in front of it does, from the database-resolved claim
/// <see cref="TokenRevocation"/> puts on the principal. What this class enforces is what must hold
/// <i>whoever</i> is asking: the self-action guard, the last-admin guard, eviction and the audit line.
/// </para>
/// <para>
/// <b>Everything that changes a cached fact evicts the key after committing.</b>
/// <see cref="TokenRevocation"/> caches <c>(UserId, SecurityStamp, IsAdmin, IsDisabled)</c> per device,
/// and the portal a per-user snapshot of the same facts, for <c>Auth:StampCacheSeconds</c> (one
/// <see cref="TokenRevocation.EvictUserAsync"/> drops both), so suspending, restoring, promoting and demoting all have to drop
/// it — <b>including promotion</b>, or a freshly-made admin waits out the TTL before the policy lets
/// them in. Evicting before the commit would only let a request already in flight re-cache the value
/// being replaced.
/// </para>
/// <para>
/// <b>Targets are addressed by the internal <c>int Id</c>.</b> That is a deliberate exception to the
/// SyncId rule in <c>CLAUDE.md</c>, explained on <see cref="AdminUserSummary"/>: the rule protects
/// against a caller widening their scope, and an admin's scope is already every user.
/// </para>
/// </summary>
public sealed class AdminOperations(
    WorkLedgerDbContext db,
    BootstrapOptions bootstrap,
    IMemoryCache cache,
    ILoggerFactory loggerFactory,
    AccountDeletion accountDeletion,
    PasswordResetService passwordResets)
{
    /// <summary>
    /// Promotes <paramref name="actorUserId"/> to admin, and only while this deployment has no admin at
    /// all, on presentation of the operator's secret.
    /// <para>
    /// <b>This is the one operation here that no admin can authorize</b> — it exists precisely for the
    /// state in which nobody holds the role. A deployment that upgraded into S10 has real users and none
    /// of them flagged, since a migration has no basis to guess which one should be. Rather than guessing
    /// (promoting the lowest id would hand the deployment to whoever registered first, which on a shared
    /// box is not necessarily its operator), the operator proves it by presenting the secret they already
    /// have in <c>.env</c>.
    /// </para>
    /// <para>
    /// <b>It takes a signed-in account AND the secret, which looks like a mistake and is not.</b> The
    /// secret says "I run this box"; the account says which user to promote and gives the action an actor
    /// to log. Either alone would be worse: secret-only has no account to point at, account-only would let
    /// any registered user promote themselves.
    /// </para>
    /// <para>
    /// <b>Self-disarming</b> — <see cref="AdminOutcome.AdminAlreadyExists"/> the moment any admin exists,
    /// so it is a one-shot door rather than a standing one. On a fresh database it is never needed at all:
    /// the first registered user is already an admin. The count is taken with the same locking read the
    /// last-admin guard uses, so two operators racing this cannot both come out promoted.
    /// </para>
    /// <para>
    /// <see cref="AdminOutcome.NoSuchUser"/> here means the <i>actor's</i> row is gone.
    /// </para>
    /// </summary>
    public async Task<AdminOutcome> BootstrapAsync(
        int actorUserId, string? providedSecret, CancellationToken cancellationToken)
    {
        if (!bootstrap.SecretMatches(providedSecret))
        {
            return AdminOutcome.BadSecret;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await CountActiveAdminsForUpdateAsync(cancellationToken) > 0)
        {
            return AdminOutcome.AdminAlreadyExists;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == actorUserId, cancellationToken);
        if (user is null)
        {
            return AdminOutcome.NoSuchUser;
        }

        user.IsAdmin = true;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Without this the caller's own next admin request is refused for up to
        // Auth:StampCacheSeconds, which reads as the bootstrap having silently failed.
        await TokenRevocation.EvictUserAsync(db, cache, actorUserId, cancellationToken);

        Audit(loggerFactory, actorUserId, "bootstrap-admin", actorUserId);
        return AdminOutcome.Done;
    }

    /// <summary>
    /// Every user on the deployment, oldest first. Ordered by id rather than by anything mutable so the
    /// list does not reshuffle under an operator between one look and the next.
    /// <para>
    /// The device count is a correlated subquery rather than a load-and-count: an operator's user list
    /// must not materialize every device row on the box to put a number in a column.
    /// </para>
    /// </summary>
    public Task<List<AdminUserSummary>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        return db.Users
            .AsNoTracking()
            .OrderBy(u => u.Id)
            .Select(u => new AdminUserSummary(
                u.Id,
                // Empty rather than null for an unclaimed pre-S6 row, matching /auth/whoami: the field
                // is non-nullable on the wire and "" reads as "not set yet".
                u.Email ?? string.Empty,
                u.DisplayName,
                u.CreatedAt,
                u.LastLoginUtc,
                db.Devices.Count(d => d.UserId == u.Id),
                u.IsAdmin,
                u.LockoutEndUtc != null && u.LockoutEndUtc > now,
                u.DisabledAtUtc != null))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Clears a lockout: zeroes <c>FailedLoginCount</c> and <c>LockoutEndUtc</c>.
    /// <see cref="AdminOutcome.Done"/> for a user who was not locked, too — the operator's intent ("let
    /// them in") is satisfied either way, and a refusal would only make them check the list first.
    /// <para>
    /// <b>This is the operation that pays for the whole operator surface.</b> Before it, the documented
    /// recovery for a locked-out user was a <c>psql</c> <c>UPDATE</c> against the volume. It also blunts
    /// the residual denial of service <see cref="CredentialCheck"/> acknowledges — anyone who knows an
    /// address can keep it locked <c>Auth:LockoutMinutes</c> at a time — though it does not remove it:
    /// the fix for that is counting failures per source rather than per account, which is a separate
    /// change.
    /// </para>
    /// <para>
    /// Nothing is evicted here, deliberately: the lockout columns are not part of the cached revocation
    /// snapshot, and the user has no live token to revive anyway.
    /// </para>
    /// </summary>
    public async Task<AdminOutcome> UnlockAsync(int actorUserId, int userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return AdminOutcome.NoSuchUser;
        }

        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        Audit(loggerFactory, actorUserId, "unlock", userId);
        return AdminOutcome.Done;
    }

    /// <summary>
    /// Suspends an account. <see cref="AdminOutcome.Done"/> for one already suspended, too.
    /// <para>
    /// Suspension keeps every row and every device; it revokes through <see cref="TokenRevocation"/> and
    /// refuses new sign-ins (<see cref="CredentialCheck"/>), and <see cref="EnableAsync"/> undoes both. It
    /// is the reversible half of what an admin can do to an account — <see cref="DeleteUserAsync"/> is the
    /// other half, and there is no way back from that one.
    /// </para>
    /// <para>
    /// <b>Subject to the self-action guard, and to the last-admin guard</b>, because an admin who cannot
    /// authenticate is not an admin: suspending the only one leaves the deployment unadministrable
    /// exactly as demoting them would. Enabling is subject to neither, for the obvious reason.
    /// </para>
    /// </summary>
    public async Task<AdminOutcome> DisableAsync(int actorUserId, int userId, CancellationToken cancellationToken)
    {
        if (userId == actorUserId)
        {
            return AdminOutcome.SelfAction;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return AdminOutcome.NoSuchUser;
        }

        if (user.DisabledAtUtc is not null)
        {
            return AdminOutcome.Done;
        }

        if (user.IsAdmin && await IsTheLastAdminAsync(cancellationToken))
        {
            return AdminOutcome.LastAdmin;
        }

        user.DisabledAtUtc = DateTime.UtcNow;
        user.UpdatedAt = user.DisabledAtUtc;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await TokenRevocation.EvictUserAsync(db, cache, userId, cancellationToken);

        Audit(loggerFactory, actorUserId, "disable", userId);
        return AdminOutcome.Done;
    }

    /// <summary>Restores a suspended account. <see cref="AdminOutcome.Done"/> for one not suspended, too.
    /// See <see cref="DisableAsync"/>.</summary>
    public async Task<AdminOutcome> EnableAsync(int actorUserId, int userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return AdminOutcome.NoSuchUser;
        }

        if (user.DisabledAtUtc is null)
        {
            return AdminOutcome.Done;
        }

        user.DisabledAtUtc = null;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // Their tokens were never destroyed, only refused — so dropping the cached "disabled"
        // snapshot is what brings existing sessions straight back rather than after the TTL.
        await TokenRevocation.EvictUserAsync(db, cache, userId, cancellationToken);

        Audit(loggerFactory, actorUserId, "enable", userId);
        return AdminOutcome.Done;
    }

    /// <summary>
    /// Grants the operator role. <see cref="AdminOutcome.Done"/> for an existing admin, too.
    /// <para>
    /// Evicts the target's cached snapshot, and needs to as much as demotion does — the policy reads the
    /// cached <c>IsAdmin</c>, so without the eviction a new admin is refused for up to
    /// <c>Auth:StampCacheSeconds</c>.
    /// </para>
    /// </summary>
    public async Task<AdminOutcome> PromoteAsync(int actorUserId, int userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return AdminOutcome.NoSuchUser;
        }

        if (user.IsAdmin)
        {
            return AdminOutcome.Done;
        }

        user.IsAdmin = true;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await TokenRevocation.EvictUserAsync(db, cache, userId, cancellationToken);

        Audit(loggerFactory, actorUserId, "promote", userId);
        return AdminOutcome.Done;
    }

    /// <summary>
    /// Withdraws the operator role. <see cref="AdminOutcome.Done"/> for a non-admin, too. Evicts, so a
    /// demoted admin stops passing the policy on their next request rather than after the TTL.
    /// <para>
    /// <b>Demotion is the one operation here an admin may aim at themselves</b>, and that is deliberate.
    /// Standing down is an ordinary thing to want, and unlike suspending or deleting yourself it has a
    /// safe outcome: you keep your account and simply lose the role. It is also what makes
    /// <see cref="IsTheLastAdminAsync"/> reachable — with the self-action guard applied here too, the
    /// actor and the target would always be two distinct active admins, the count could never be one,
    /// and the last-admin guard would be dead code protecting an invariant the self-guard was already
    /// holding by accident. Allowing it puts the invariant where it can be stated and tested.
    /// </para>
    /// </summary>
    public async Task<AdminOutcome> DemoteAsync(int actorUserId, int userId, CancellationToken cancellationToken)
    {
        // Deliberately NOT subject to the self-action guard, unlike disable and delete — see above.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return AdminOutcome.NoSuchUser;
        }

        if (!user.IsAdmin)
        {
            return AdminOutcome.Done;
        }

        if (await IsTheLastAdminAsync(cancellationToken))
        {
            return AdminOutcome.LastAdmin;
        }

        user.IsAdmin = false;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await TokenRevocation.EvictUserAsync(db, cache, userId, cancellationToken);

        Audit(loggerFactory, actorUserId, "demote", userId);
        return AdminOutcome.Done;
    }

    /// <summary>
    /// Deletes someone else's account and everything under it, through <see cref="AccountDeletion"/> —
    /// the same immediate, total, irreversible pipeline a user runs on their own account.
    /// <para>
    /// <b>One gate, not two, and it is the one that matters here.</b> Self-service deletion re-verifies
    /// the owner's password because a stolen token must not be enough to destroy an account. An admin does
    /// not have the target's password, and demanding their own would confirm <i>who is asking</i> while
    /// confirming nothing about <i>which account is about to go</i> — which is the mistake worth
    /// preventing when the operator is looking at a list of other people. So the gate is
    /// <paramref name="confirmEmail"/>, matched against the target by
    /// <see cref="AccountDeletion.ConfirmationMatches"/>: the id and the address have to agree, and a stale
    /// list cannot delete the wrong person. A null <paramref name="confirmEmail"/> means none was offered
    /// at all (<see cref="AdminOutcome.ConfirmationMissing"/>); a blank or wrong one is
    /// <see cref="AdminOutcome.ConfirmationMismatch"/>.
    /// </para>
    /// <para>
    /// Subject to the last-admin guard and to the self-action guard — an admin who wants to leave uses
    /// self-service deletion, which re-verifies their password, rather than the operation built for
    /// acting on other people.
    /// </para>
    /// </summary>
    public async Task<AdminOutcome> DeleteUserAsync(
        int actorUserId, int userId, string? confirmEmail, CancellationToken cancellationToken)
    {
        if (userId == actorUserId)
        {
            return AdminOutcome.SelfAction;
        }

        if (confirmEmail is null)
        {
            return AdminOutcome.ConfirmationMissing;
        }

        // Opened here, not by AccountDeletion, so the last-admin guard's lock is held until the delete
        // commits under it. AccountDeletion joins this transaction and commits it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return AdminOutcome.NoSuchUser;
        }

        if (!AccountDeletion.ConfirmationMatches(user, confirmEmail))
        {
            return AdminOutcome.ConfirmationMismatch;
        }

        if (user.IsAdmin && await IsTheLastAdminAsync(cancellationToken))
        {
            return AdminOutcome.LastAdmin;
        }

        await accountDeletion.DeleteUserAsync(userId, actorUserId, cancellationToken);
        return AdminOutcome.Done;
    }

    /// <summary>
    /// Mints a password reset for <paramref name="userId"/> through <see cref="PasswordResetService"/>,
    /// whose doc covers what the token can and cannot do. The token comes back <b>once</b>, for the admin
    /// to hand over out of band.
    /// <para>
    /// A credential-less pre-S6 row is <see cref="AdminOutcome.NoCredentials"/>: setting a password on a
    /// row with no email would produce an account that still cannot sign in, so the token would be useless
    /// and the error would arrive far from its cause.
    /// </para>
    /// <para>
    /// A <b>suspended</b> user is deliberately <i>not</i> refused. Resetting then re-enabling is a
    /// reasonable order to do things in, and the token is worth nothing on its own while
    /// <c>DisabledAtUtc</c> is set — redeeming it changes the password and no more.
    /// </para>
    /// </summary>
    public async Task<(AdminOutcome Outcome, IssuedPasswordReset? Reset)> IssuePasswordResetAsync(
        int actorUserId, int userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return (AdminOutcome.NoSuchUser, null);
        }

        if (user.PasswordHash is null)
        {
            return (AdminOutcome.NoCredentials, null);
        }

        var reset = await passwordResets.IssueAsync(userId, actorUserId, cancellationToken);

        Audit(loggerFactory, actorUserId, "issue-password-reset", userId);
        return (AdminOutcome.Done, reset);
    }

    /// <summary>
    /// Whether the admin being acted on is the only one who can still sign in — an admin row that is
    /// itself suspended does not count, because it cannot administer anything. Every caller has already
    /// checked that the target is an admin, so "the count is 1" means "that one is the target".
    /// <para>
    /// <b>Call this inside a transaction, and only from a path that then writes in the same one.</b>
    /// The locking read below is what makes the answer hold until the caller commits; a plain
    /// <c>COUNT</c> at read-committed would let two demotions of two different admins each see two and
    /// both succeed, leaving zero.
    /// </para>
    /// <para>
    /// <b>In practice only self-demotion reaches a count of one.</b> The caller is always an active
    /// admin, so whenever the target is somebody else there are at least two and this returns false.
    /// The check is still made on all three paths rather than only on demotion: it is the statement of
    /// the invariant, and it should not quietly stop being enforced the day a caller is added who is not
    /// the person the row belongs to.
    /// </para>
    /// </summary>
    private async Task<bool> IsTheLastAdminAsync(CancellationToken cancellationToken) =>
        await CountActiveAdminsForUpdateAsync(cancellationToken) <= 1;

    /// <summary>
    /// The admin rows, locked <c>FOR UPDATE</c> for the rest of the caller's transaction.
    /// <para>
    /// <b>Not composed with LINQ on purpose.</b> Adding a <c>Select</c> or <c>Count</c> would make EF
    /// wrap this in a subquery, and <c>FOR UPDATE</c> has to stay on the outer statement to lock what
    /// it names. Materializing a handful of rows is the cheaper mistake; the partial index in
    /// <c>WorkLedgerDbContext</c> is what keeps it from scanning every user to find them.
    /// </para>
    /// <para>
    /// Postgres re-evaluates the predicate after a contended lock is granted, so a second transaction
    /// that was waiting sees the post-commit truth rather than its own stale snapshot. That is what
    /// makes this a guard rather than a race with extra steps.
    /// </para>
    /// </summary>
    private async Task<int> CountActiveAdminsForUpdateAsync(CancellationToken cancellationToken)
    {
        var admins = await db.Users
            .FromSql($"""SELECT * FROM "Users" WHERE "IsAdmin" AND "DisabledAtUtc" IS NULL FOR UPDATE""")
            .ToListAsync(cancellationToken);
        return admins.Count;
    }

    /// <summary>
    /// One line per admin write: who did it, what, and to whom. <b>Ids, never emails</b> — the request
    /// log in <c>Program.cs</c> records method, path and status and nothing else precisely so that no
    /// secret or identity lands in it, and admin actions are where that discipline is most tempting to
    /// relax. An id reconciles against a backup; an address puts a person in a log file that holds none.
    /// </summary>
    internal static void Audit(ILoggerFactory loggerFactory, int actorUserId, string action, int targetUserId) =>
        loggerFactory.CreateLogger("AdminAction").LogInformation(
            "Admin {ActorUserId} performed {Action} on user {UserId}.", actorUserId, action, targetUserId);

    /// <summary>
    /// The same line for <see cref="InviteService"/>'s admin actions, whose target is an invite rather
    /// than a user: its row id, which is what <c>DELETE /admin/invites/{id}</c> names and what the list
    /// shows. Never the token, and never the bound address.
    /// </summary>
    internal static void AuditInvite(ILoggerFactory loggerFactory, int actorUserId, string action, int inviteId) =>
        loggerFactory.CreateLogger("AdminAction").LogInformation(
            "Admin {ActorUserId} performed {Action} on invite {InviteId}.", actorUserId, action, inviteId);
}

/// <summary>What an <see cref="AdminOperations"/> method did, or which rule stopped it.</summary>
public enum AdminOutcome
{
    /// <summary>Done — including a no-op, where the target was already in the requested state.</summary>
    Done,

    /// <summary>No user with that id.</summary>
    NoSuchUser,

    /// <summary>The actor aimed an operation at themselves that only acts on other people.</summary>
    SelfAction,

    /// <summary>It would leave the deployment with no admin who can sign in.</summary>
    LastAdmin,

    /// <summary>The target is the credential-less pre-S6 row, which has nothing to reset.</summary>
    NoCredentials,

    /// <summary>Deletion was asked for with no typed confirmation at all.</summary>
    ConfirmationMissing,

    /// <summary>The typed confirmation is blank or is not the target's email.</summary>
    ConfirmationMismatch,

    /// <summary>Bootstrap only: the secret presented is not the operator's.</summary>
    BadSecret,

    /// <summary>Bootstrap only: the deployment already has an admin, so the door is shut.</summary>
    AdminAlreadyExists,
}
