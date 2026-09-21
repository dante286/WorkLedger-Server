using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Auth;

/// <summary>
/// The operator surface (S10). Everything a person running this deployment for other people needs to
/// do to an account that is not their own: see who exists, lift a lockout, suspend and restore, hand
/// out and take back admin, and delete.
/// <para>
/// <b>Why this exists at all, given <see cref="AccountEndpoints"/> says it should not.</b> That file
/// states there is no admin surface because "an operator with shell access has <c>psql</c>", and while
/// the only user <i>was</i> the operator that was true and sufficient. It stops being either once other
/// people have accounts: the documented recovery for a forgotten password or a lockout became an
/// operator opening a shell against the volume, per incident, for someone else's account. These routes
/// are that recovery, made ordinary.
/// </para>
/// <para>
/// <b>Authority comes from the database, not the token.</b> Every route here is
/// <c>.RequireAuthorization(TokenRevocation.AdminPolicy)</c>, and that policy's claim is put on the
/// principal by <see cref="TokenRevocation"/> from the <c>IsAdmin</c> column on each request — see its
/// doc for why a year-long token must not carry a privilege. A non-admin gets <c>403</c>, not
/// <c>404</c>: these routes are documented in the README, so hiding them buys no secrecy and costs an
/// operator a confusing session.
/// </para>
/// <para>
/// <b>Targets are addressed by the internal <c>int Id</c>.</b> That is a deliberate exception to the
/// SyncId rule in <c>CLAUDE.md</c>, explained on <see cref="AdminUserSummary"/>: the rule protects
/// against a caller widening their scope, and an admin's scope is already every user.
/// </para>
/// <para>
/// <b>Everything that changes a cached fact evicts the key after committing.</b>
/// <see cref="TokenRevocation"/> caches <c>(UserId, SecurityStamp, IsAdmin, IsDisabled)</c> per device
/// for <c>Auth:StampCacheSeconds</c>, so suspending, restoring, promoting and demoting all have to drop
/// it — <b>including promotion</b>, or a freshly-made admin waits out the TTL before the policy lets
/// them in. Evicting before the commit would only let a request already in flight re-cache the value
/// being replaced.
/// </para>
/// <para>
/// <b>Not here yet.</b> Invite management (<c>/admin/invites</c>) ships with the invite entity itself,
/// and <c>POST /admin/users/{id}/password-reset</c> with the password-reset table — both are admin
/// routes but neither has anything to read or write until those land.
/// </para>
/// </summary>
public static class AdminEndpoints
{
    /// <summary>
    /// Mapped from <see cref="AuthEndpoints.MapAuthEndpoints"/>, so the whole auth surface is still
    /// registered in one call.
    /// </summary>
    public static void MapAdminEndpoints(this WebApplication app)
    {
        MapBootstrap(app);
        MapListUsers(app);
        MapUnlockUser(app);
        MapSetUserDisabled(app);
        MapSetUserAdmin(app);
        MapDeleteUser(app);
    }

    /// <summary>
    /// Promotes the <b>calling</b> user to admin, and only while this deployment has no admin at all.
    /// <para>
    /// <b>The one route in this file that is not behind the admin policy</b> — it cannot be, because it
    /// exists precisely for the state in which nobody can satisfy that policy. A deployment that
    /// upgraded into S10 has real users and none of them flagged, since a migration has no basis to
    /// guess which one should be. Rather than guessing (promoting the lowest id would hand the
    /// deployment to whoever registered first, which on a shared box is not necessarily its operator),
    /// the operator proves it by presenting the secret they already have in <c>.env</c>.
    /// </para>
    /// <para>
    /// <b>It takes a token AND the secret, which looks like a mistake and is not.</b> The secret says
    /// "I run this box"; the token says which account to promote and gives the action an actor to log.
    /// Either alone would be worse: secret-only has no account to point at, token-only would let any
    /// registered user promote themselves.
    /// </para>
    /// <para>
    /// <b>Self-disarming</b> — <c>409</c> the moment any admin exists, so it is a one-shot door rather
    /// than a standing one. <c>401</c> for a bad secret. On a fresh database it is never needed at all:
    /// the first registered user is already an admin.
    /// </para>
    /// <para>
    /// The count is taken with the same locking read the last-admin guard uses, so two operators racing
    /// this cannot both come out promoted.
    /// </para>
    /// </summary>
    private static void MapBootstrap(WebApplication app) =>
        app.MapPost("/admin/bootstrap", async (
            HttpRequest httpRequest,
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            BootstrapOptions bootstrap,
            IMemoryCache cache,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (!SecretMatches(httpRequest, bootstrap))
            {
                return Results.Unauthorized();
            }

            var actorId = currentContext.UserId;

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            if (await CountActiveAdminsForUpdateAsync(db, cancellationToken) > 0)
            {
                return AuthEndpoints.Error(
                    StatusCodes.Status409Conflict,
                    "This server already has an administrator. Ask them to promote you with " +
                    "POST /admin/users/{id}/admin.");
            }

            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == actorId, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            user.IsAdmin = true;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Without this the caller's own next /admin call is a 403 for up to Auth:StampCacheSeconds,
            // which reads as the bootstrap having silently failed.
            await TokenRevocation.EvictUserAsync(db, cache, actorId, cancellationToken);

            Audit(loggerFactory, actorId, "bootstrap-admin", actorId);
            return Results.NoContent();
        });

    /// <summary>
    /// Every user on the deployment, oldest first. Ordered by id rather than by anything mutable so the
    /// list a client renders does not reshuffle under an operator between one call and the next.
    /// <para>
    /// The device count is a correlated subquery rather than a load-and-count: an operator's user list
    /// must not materialize every device row on the box to put a number in a column.
    /// </para>
    /// </summary>
    private static void MapListUsers(WebApplication app) =>
        app.MapGet("/admin/users", async (
            WorkLedgerDbContext db,
            CancellationToken cancellationToken) =>
        {
            var now = DateTime.UtcNow;
            var users = await db.Users
                .AsNoTracking()
                .OrderBy(u => u.Id)
                .Select(u => new AdminUserSummary(
                    u.Id,
                    // Empty rather than null for an unclaimed pre-S6 row, matching /auth/whoami: the
                    // field is non-nullable on the wire and "" reads as "not set yet".
                    u.Email ?? string.Empty,
                    u.DisplayName,
                    u.CreatedAt,
                    u.LastLoginUtc,
                    db.Devices.Count(d => d.UserId == u.Id),
                    u.IsAdmin,
                    u.LockoutEndUtc != null && u.LockoutEndUtc > now,
                    u.DisabledAtUtc != null))
                .ToListAsync(cancellationToken);

            return Results.Ok(users);
        }).RequireAuthorization(TokenRevocation.AdminPolicy);

    /// <summary>
    /// Clears a lockout: zeroes <c>FailedLoginCount</c> and <c>LockoutEndUtc</c>. <c>204</c>, and
    /// <c>204</c> again for a user who was not locked — the operator's intent ("let them in") is
    /// satisfied either way, and a <c>409</c> would only make them check the list first.
    /// <para>
    /// <b>This is the endpoint that pays for the whole file.</b> Before it, the documented recovery for
    /// a locked-out user was a <c>psql</c> <c>UPDATE</c> against the volume. It also blunts the residual
    /// denial of service <c>AuthEndpoints</c>' login doc acknowledges — anyone who knows an address can
    /// keep it locked <c>Auth:LockoutMinutes</c> at a time — though it does not remove it: the fix for
    /// that is counting failures per source rather than per account, which is a separate change.
    /// </para>
    /// <para>
    /// Nothing is evicted here, deliberately: the lockout columns are not part of the cached revocation
    /// snapshot, and the user has no live token to revive anyway.
    /// </para>
    /// </summary>
    private static void MapUnlockUser(WebApplication app) =>
        app.MapPost("/admin/users/{userId:int}/unlock", async (
            int userId,
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
            {
                return NoSuchUser();
            }

            user.FailedLoginCount = 0;
            user.LockoutEndUtc = null;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            Audit(loggerFactory, currentContext.UserId, "unlock", userId);
            return Results.NoContent();
        }).RequireAuthorization(TokenRevocation.AdminPolicy);

    /// <summary>
    /// Suspends (<c>POST .../disable</c>) or restores (<c>POST .../enable</c>) an account. <c>204</c>,
    /// and <c>204</c> for a no-op in either direction.
    /// <para>
    /// Suspension keeps every row and every device; it revokes through
    /// <see cref="TokenRevocation"/> and refuses new logins, and restoring undoes both. It is the
    /// reversible half of what this file can do to an account — <c>DELETE /admin/users/{id}</c> is the
    /// other half, and there is no way back from that one.
    /// </para>
    /// <para>
    /// <b>Disabling is subject to the last-admin guard</b>, because an admin who cannot authenticate is
    /// not an admin: suspending the only one leaves the deployment unadministrable exactly as demoting
    /// them would. Enabling is not, for the obvious reason.
    /// </para>
    /// </summary>
    private static void MapSetUserDisabled(WebApplication app)
    {
        app.MapPost("/admin/users/{userId:int}/disable", async (
            int userId,
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            IMemoryCache cache,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var actorId = currentContext.UserId;
            if (userId == actorId)
            {
                return SelfAction("suspend your own account");
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
            {
                return NoSuchUser();
            }

            if (user.DisabledAtUtc is not null)
            {
                return Results.NoContent();
            }

            if (user.IsAdmin && await IsTheLastAdminAsync(db, userId, cancellationToken))
            {
                return LastAdmin("suspend");
            }

            user.DisabledAtUtc = DateTime.UtcNow;
            user.UpdatedAt = user.DisabledAtUtc;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await TokenRevocation.EvictUserAsync(db, cache, userId, cancellationToken);

            Audit(loggerFactory, actorId, "disable", userId);
            return Results.NoContent();
        }).RequireAuthorization(TokenRevocation.AdminPolicy);

        app.MapPost("/admin/users/{userId:int}/enable", async (
            int userId,
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            IMemoryCache cache,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
            {
                return NoSuchUser();
            }

            if (user.DisabledAtUtc is null)
            {
                return Results.NoContent();
            }

            user.DisabledAtUtc = null;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            // Their tokens were never destroyed, only refused — so dropping the cached "disabled"
            // snapshot is what brings existing sessions straight back rather than after the TTL.
            await TokenRevocation.EvictUserAsync(db, cache, userId, cancellationToken);

            Audit(loggerFactory, currentContext.UserId, "enable", userId);
            return Results.NoContent();
        }).RequireAuthorization(TokenRevocation.AdminPolicy);
    }

    /// <summary>
    /// Grants (<c>POST .../admin</c>) or withdraws (<c>DELETE .../admin</c>) the operator role.
    /// <c>204</c>, and <c>204</c> for a no-op in either direction.
    /// <para>
    /// Both evict the target's cached snapshot. Promotion needs it as much as demotion does — the
    /// policy reads the cached <c>IsAdmin</c>, so without the eviction a new admin is refused for up to
    /// <c>Auth:StampCacheSeconds</c> and a demoted one keeps working for just as long.
    /// </para>
    /// <para>
    /// <b>Demotion is the one action here an admin may aim at themselves</b>, and that is deliberate.
    /// Standing down is an ordinary thing to want, and unlike suspending or deleting yourself it has a
    /// safe outcome: you keep your account and simply lose the role. It is also what makes
    /// <see cref="IsTheLastAdminAsync"/> reachable — with the self-action guard applied here too, the
    /// actor and the target would always be two distinct active admins, the count could never be one,
    /// and the last-admin guard would be dead code protecting an invariant the self-guard was already
    /// holding by accident. Allowing it puts the invariant where it can be stated and tested.
    /// </para>
    /// </summary>
    private static void MapSetUserAdmin(WebApplication app)
    {
        app.MapPost("/admin/users/{userId:int}/admin", async (
            int userId,
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            IMemoryCache cache,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
            {
                return NoSuchUser();
            }

            if (user.IsAdmin)
            {
                return Results.NoContent();
            }

            user.IsAdmin = true;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            await TokenRevocation.EvictUserAsync(db, cache, userId, cancellationToken);

            Audit(loggerFactory, currentContext.UserId, "promote", userId);
            return Results.NoContent();
        }).RequireAuthorization(TokenRevocation.AdminPolicy);

        app.MapDelete("/admin/users/{userId:int}/admin", async (
            int userId,
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            IMemoryCache cache,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var actorId = currentContext.UserId;

            // Deliberately NOT subject to the self-action guard, unlike disable and delete. Standing
            // down is a legitimate thing for an admin to do, and it is also the only action that can
            // reach the last-admin guard at all — see that guard's doc.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
            {
                return NoSuchUser();
            }

            if (!user.IsAdmin)
            {
                return Results.NoContent();
            }

            if (await IsTheLastAdminAsync(db, userId, cancellationToken))
            {
                return LastAdmin("demote");
            }

            user.IsAdmin = false;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await TokenRevocation.EvictUserAsync(db, cache, userId, cancellationToken);

            Audit(loggerFactory, actorId, "demote", userId);
            return Results.NoContent();
        }).RequireAuthorization(TokenRevocation.AdminPolicy);
    }

    /// <summary>
    /// Deletes someone else's account and everything under it — the same immediate, total, irreversible
    /// operation <c>DELETE /auth/account</c> performs on your own, and documented in full there.
    /// <para>
    /// <b>One gate, not two, and it is the one that matters here.</b> The self-service route
    /// re-verifies the caller's password because a stolen token must not be enough to destroy an
    /// account. An admin does not have the target's password, and demanding their own would confirm
    /// <i>who is asking</i> while confirming nothing about <i>which account is about to go</i> — which
    /// is the mistake worth preventing when the operator is looking at a list of other people. So the
    /// gate is <see cref="AdminDeleteUserRequest.ConfirmEmail"/>, matched against the target
    /// case-insensitively: the id and the address have to agree, and a stale list cannot delete the
    /// wrong person.
    /// </para>
    /// <para>
    /// Subject to the last-admin guard and to the self-action guard, the latter naming
    /// <c>DELETE /auth/account</c> — an admin who wants to leave uses the route that re-verifies their
    /// password, rather than the one built for acting on other people.
    /// </para>
    /// </summary>
    private static void MapDeleteUser(WebApplication app) =>
        app.MapDelete("/admin/users/{userId:int}", async (
            int userId,
            // Explicit and nullable for the same two reasons DELETE /auth/account gives: minimal APIs
            // refuse an inferred body on DELETE, and a non-nullable one turns an absent body into the
            // framework's own 400 instead of a message naming the field.
            [FromBody] AdminDeleteUserRequest? request,
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            IMemoryCache cache,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var actorId = currentContext.UserId;
            if (userId == actorId)
            {
                return AuthEndpoints.Error(
                    StatusCodes.Status409Conflict,
                    "You cannot delete your own account from the admin routes. Use DELETE /auth/account, " +
                    "which re-verifies your password.");
            }

            if (request is null)
            {
                return Results.BadRequest(
                    new { error = "A request body with the target account's email is required." });
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
            {
                return NoSuchUser();
            }

            var confirmEmail = (request.ConfirmEmail?.Trim() ?? string.Empty).ToUpperInvariant();
            if (confirmEmail.Length == 0 ||
                !string.Equals(confirmEmail, user.NormalizedEmail, StringComparison.Ordinal))
            {
                return AuthEndpoints.Error(
                    StatusCodes.Status400BadRequest,
                    "ConfirmEmail does not match that account's email address.");
            }

            if (user.IsAdmin && await IsTheLastAdminAsync(db, userId, cancellationToken))
            {
                return LastAdmin("delete");
            }

            // Read before the delete: EvictUserAsync finds its keys by querying Devices, and by the
            // time this commits there is no row left to find.
            var deviceRowIds = await db.Devices
                .Where(d => d.UserId == userId)
                .Select(d => d.Id)
                .ToListAsync(cancellationToken);

            // Children first, one statement per table — see DELETE /auth/account for why the explicit
            // order is kept even though the FKs are ON DELETE CASCADE.
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

            foreach (var deviceRowId in deviceRowIds)
            {
                cache.Remove(TokenRevocation.CacheKey(deviceRowId));
            }

            loggerFactory.CreateLogger("AdminAction").LogWarning(
                "Admin {ActorUserId} deleted account {UserId}: {TimeEntries} time entries, " +
                "{TaskItems} tasks, {Projects} projects, {Devices} devices, {Users} user row(s).",
                actorId, userId, timeEntries, taskItems, projects, devices, users);

            return Results.NoContent();
        }).RequireAuthorization(TokenRevocation.AdminPolicy);

    /// <summary>
    /// Whether <paramref name="userId"/> is the only admin who can still sign in — an admin row that is
    /// itself suspended does not count, because it cannot administer anything.
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
    /// the invariant, and it should not quietly stop being enforced the day a route is added whose
    /// caller is not the person the row belongs to.
    /// </para>
    /// </summary>
    private static async Task<bool> IsTheLastAdminAsync(
        WorkLedgerDbContext db, int userId, CancellationToken cancellationToken)
    {
        var admins = await CountActiveAdminsForUpdateAsync(db, cancellationToken);
        return admins <= 1;

        // Note the caller always checks user.IsAdmin first, so "the count is 1" means "that one is the
        // target". A demotion of a non-admin never reaches here.
    }

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
    private static async Task<int> CountActiveAdminsForUpdateAsync(
        WorkLedgerDbContext db, CancellationToken cancellationToken)
    {
        var admins = await db.Users
            .FromSql($"""SELECT * FROM "Users" WHERE "IsAdmin" AND "DisabledAtUtc" IS NULL FOR UPDATE""")
            .ToListAsync(cancellationToken);
        return admins.Count;
    }

    /// <summary>
    /// One line per admin write: who did it, what, and to whom. <b>Ids, never emails</b> — the request
    /// log in <c>Program.cs</c> records method, path and status and nothing else precisely so that no
    /// secret or identity lands in it, and these routes are where that discipline is most tempting to
    /// relax. An id reconciles against a backup; an address puts a person in a log file that holds none.
    /// </summary>
    private static void Audit(ILoggerFactory loggerFactory, int actorUserId, string action, int targetUserId) =>
        loggerFactory.CreateLogger("AdminAction").LogInformation(
            "Admin {ActorUserId} performed {Action} on user {UserId}.", actorUserId, action, targetUserId);

    /// <summary>
    /// <c>404</c> for an id that matches nobody. Unlike the report and device routes, this is not
    /// hiding anything — an admin may see every user — it just has nothing to act on.
    /// </summary>
    private static IResult NoSuchUser() =>
        AuthEndpoints.Error(StatusCodes.Status404NotFound, "No user with that id.");

    private static IResult SelfAction(string what) =>
        AuthEndpoints.Error(
            StatusCodes.Status409Conflict,
            $"You cannot {what} from the admin routes. These act on other users; " +
            "use the routes under /auth for your own account.");

    /// <summary>
    /// The refusal that keeps the deployment administrable. It names the fix, because the caller of a
    /// self-demotion is by definition the only person who can carry it out.
    /// </summary>
    private static IResult LastAdmin(string what) =>
        AuthEndpoints.Error(
            StatusCodes.Status409Conflict,
            $"Cannot {what} the last administrator — this server would be left with none, and " +
            "POST /admin/bootstrap refuses once any account exists. Promote another user first.");

    /// <summary>Constant-time, exactly as <see cref="AuthEndpoints"/> compares it. See there for why.</summary>
    private static bool SecretMatches(HttpRequest httpRequest, BootstrapOptions bootstrap) =>
        httpRequest.Headers.TryGetValue("X-Bootstrap-Secret", out var provided) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided.ToString()),
            Encoding.UTF8.GetBytes(bootstrap.Secret));
}
