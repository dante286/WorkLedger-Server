using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Auth;

/// <summary>
/// The one pipeline that deletes an account and everything under it. <b>Immediate, total and
/// irreversible</b> — hard delete, not a flag: a soft delete would put an <c>AND NotDeleted</c> term in
/// every sync and report query for a case that arises once per account, and "your data is still on the
/// server but we promise not to look" is not what someone asking to delete their account means. A grace
/// period is the follow-up worth having and is a deliberate deferral, not an oversight.
/// <para>
/// <b>This has no gates of its own, deliberately.</b> Who may delete which account, and what they must
/// prove first, differs by caller and is decided there: <see cref="AccountSelfService"/> re-verifies the
/// owner's password and a typed email; <see cref="AdminOperations"/> takes a typed email plus the
/// self-action and last-admin guards. What must <i>not</i> differ — the order, the transaction, the
/// eviction and the log line — lives here once, because it used to be written twice.
/// </para>
/// <para>
/// <b>Children first, one <c>ExecuteDeleteAsync</c> per table, inside one explicit transaction.</b>
/// The FKs are <c>ON DELETE CASCADE</c>, so deleting the user row alone would in fact clear
/// everything — the explicit order is what makes this independent of that configuration, and it is
/// what produces the per-table counts the log line reports. <c>ExecuteDeleteAsync</c> is one
/// statement per table with no entity materialization, so an account with years of time entries does
/// not have to be loaded into memory to be deleted.
/// </para>
/// <para>
/// <b>A caller that needs a guard inside the same transaction opens it first.</b> The last-admin guard
/// takes a <c>FOR UPDATE</c> lock that only means anything if the delete commits under it, so
/// <see cref="DeleteUserAsync"/> joins a transaction the caller already holds rather than opening a
/// second one, and <b>commits it either way</b> — the eviction below has to follow the commit, so the
/// commit cannot be left to the caller.
/// </para>
/// <para>
/// <b>A sync push racing this cannot resurrect rows.</b> The push holds its own transaction and its
/// inserts take an FK lock on the user row, so it either commits first — and its rows are then inside
/// this delete's <c>WHERE UserId</c> — or commits after the user row is gone, where the FK refuses
/// it and the push fails (a <c>500</c> at worst, for a user who no longer exists). Either way the
/// database ends empty for this user. No explicit row lock is taken because none is needed for that
/// invariant; if this ever proves otherwise, a <c>SELECT ... FOR UPDATE</c> on the user row at the
/// top is the fix.
/// </para>
/// <para>
/// <b>The log line carries ids and counts, never the email.</b> The request log in <c>Program.cs</c>
/// deliberately records method, path and status and nothing else, and this — the one irreversible
/// action in the API — is exactly the place tempted to widen that. The id is enough to reconcile against
/// a backup; the address would put a person's identity in a log file that otherwise holds none. A
/// self-deletion logs under <c>AccountDeletion</c>; an admin's under <c>AdminAction</c>, with the actor,
/// alongside that category's other audit lines.
/// </para>
/// </summary>
internal sealed class AccountDeletion(WorkLedgerDbContext db, IMemoryCache cache, ILoggerFactory loggerFactory)
{
    /// <summary>
    /// Deletes <paramref name="userId"/> and every row it owns. <paramref name="actorUserId"/> is the
    /// admin doing it, or null when the account's owner is.
    /// </summary>
    public async Task DeleteUserAsync(int userId, int? actorUserId, CancellationToken cancellationToken)
    {
        // Null when the caller already holds one; `await using` on null is a no-op, so the caller's own
        // `await using` stays the one that disposes it.
        await using var ownTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var transaction = db.Database.CurrentTransaction!;

        // Read before the delete, not after: TokenRevocation.EvictUserAsync finds the keys to drop by
        // querying Devices, and by the time this commits there is no row left to find. So this evicts by
        // hand below — the device keys and the per-user key, the same set EvictUserAsync would drop.
        var deviceRowIds = await db.Devices
            .Where(d => d.UserId == userId)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        var timeEntries = await db.TimeEntries.Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        var taskItems = await db.TaskItems.Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        var projects = await db.Projects.Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        var devices = await db.Devices.Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        var users = await db.Users.Where(x => x.Id == userId)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // After the commit, never before: evicting first would only let a request already in flight
        // re-cache the snapshot of a user this call is removing.
        foreach (var deviceRowId in deviceRowIds)
        {
            cache.Remove(TokenRevocation.CacheKey(deviceRowId));
        }

        cache.Remove(TokenRevocation.UserCacheKey(userId));

        if (actorUserId is { } actor)
        {
            loggerFactory.CreateLogger("AdminAction").LogWarning(
                "Admin {ActorUserId} deleted account {UserId}: {TimeEntries} time entries, " +
                "{TaskItems} tasks, {Projects} projects, {Devices} devices, {Users} user row(s).",
                actor, userId, timeEntries, taskItems, projects, devices, users);
        }
        else
        {
            loggerFactory.CreateLogger("AccountDeletion").LogWarning(
                "Deleted account {UserId}: {TimeEntries} time entries, {TaskItems} tasks, " +
                "{Projects} projects, {Devices} devices, {Users} user row(s).",
                userId, timeEntries, taskItems, projects, devices, users);
        }
    }

    /// <summary>
    /// The typed-confirmation gesture both deletion paths ask for: <paramref name="confirmEmail"/> must be
    /// <paramref name="user"/>'s address, case-insensitively. Blank never matches — not even a
    /// credential-less row's null address. Shared so the self-service and admin gates cannot compare
    /// differently.
    /// </summary>
    public static bool ConfirmationMatches(User user, string? confirmEmail)
    {
        var normalized = (confirmEmail?.Trim() ?? string.Empty).ToUpperInvariant();
        return normalized.Length != 0 &&
            string.Equals(normalized, user.NormalizedEmail, StringComparison.Ordinal);
    }
}
