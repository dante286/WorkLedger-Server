using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Auth;

/// <summary>
/// What a signed-in person can do to their own account and to the devices attached to it: list them,
/// detach one, sign them all out, or delete the lot. S6 built the user row and S7 gave outstanding
/// tokens a kill switch (<see cref="TokenRevocation"/>); this is what spends that mechanism.
/// <para>
/// <b>Every route here is authenticated and scoped entirely by the token.</b> None of them accepts a
/// user identifier from a body or a URL — the tenancy invariant in <c>CLAUDE.md</c> — so the worst a
/// caller can do with a crafted request is address something of their own.
/// </para>
/// <para>
/// <b>S10 added an operator surface, and it did not change that.</b> <see cref="AdminEndpoints"/> does
/// take target ids, but it is a different file behind a different policy; nothing here grew a way to
/// name another user. The division is worth keeping: these four routes remain provably
/// self-scoped, which is what makes them safe to reason about in isolation. An earlier version of this
/// doc said there was no admin surface because no role could see another user's devices — that was
/// true until S10 and is not any more. <c>GET /admin/users</c> reports device <i>counts</i>; no admin
/// route lists another user's devices, or reads any of their data.
/// </para>
/// <para>
/// <b>Revocation is evicted, not waited out.</b> Three of these four endpoints kill tokens, and each
/// drops the affected <see cref="TokenRevocation"/> cache entries <i>after</i> its write commits, so the
/// kill lands on the very next request instead of up to <c>Auth:StampCacheSeconds</c> later. Evicting
/// before the commit would only let a request already in flight re-cache the value being replaced.
/// </para>
/// </summary>
public static class AccountEndpoints
{
    /// <summary>
    /// Mapped from <see cref="AuthEndpoints.MapAuthEndpoints"/> rather than from <c>Program.cs</c>, so
    /// the auth surface is still registered in one call. Nothing here opts out of the fallback policy.
    /// </summary>
    public static void MapAccountEndpoints(this WebApplication app)
    {
        MapListDevices(app);
        MapDeleteDevice(app);
        MapLogoutAll(app);
        MapDeleteAccount(app);
    }

    /// <summary>
    /// The devices signed in to this account, most recently seen first, so a client can render a
    /// "where am I signed in" screen. Scoped to the token's user; another user's devices are not
    /// merely hidden but unreachable, since the query never sees a caller-supplied user id.
    /// <para>
    /// <b>Ordering puts never-seen devices last, not first.</b> Postgres sorts nulls first under
    /// <c>DESC</c>, which would float a row that has never made a request above the device the user is
    /// looking at the list from. <c>LastSeenUtc</c> is written on every login and so is null only for a
    /// row created some other way, but the fix costs one term and the alternative reads as a bug.
    /// </para>
    /// </summary>
    private static void MapListDevices(WebApplication app) =>
        app.MapGet("/auth/devices", async (
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            CancellationToken cancellationToken) =>
        {
            var currentDeviceId = currentContext.DeviceId;
            var devices = await db.Devices
                .Where(d => d.UserId == currentContext.UserId)
                .OrderByDescending(d => d.LastSeenUtc.HasValue)
                .ThenByDescending(d => d.LastSeenUtc)
                // A stable tiebreak so two devices seen in the same millisecond do not swap places
                // between calls and make the list flicker.
                .ThenBy(d => d.Id)
                .Select(d => new DeviceSummary(
                    d.DeviceId, d.Label, d.CreatedAt, d.LastSeenUtc, d.DeviceId == currentDeviceId))
                .ToListAsync(cancellationToken);

            return Results.Ok(devices);
        });

    /// <summary>
    /// Detaches one device from this account: <c>204</c>, or <c>404</c> for a <c>deviceId</c> that is
    /// unknown <i>or belongs to another user</i> — the same answer the report routes give for a foreign
    /// <c>SyncId</c>, and for the same reason: a distinguishable "exists but not yours" would confirm
    /// another account's device id to anyone willing to guess.
    /// <para>
    /// <b>The device's synced rows are deliberately left alone.</b> This is the single most plausible
    /// wrong assumption about this endpoint, so: projects, tasks and time entries belong to the
    /// <i>user</i>, not to the device that happened to create them. <c>OriginDeviceId</c> on those rows
    /// is provenance — it tells a pull which changes to echo back — not ownership. Deleting the rows a
    /// retired laptop originated would destroy history every other device is still syncing. Removing a
    /// device revokes its access and nothing else.
    /// </para>
    /// <para>
    /// <b>Removing your own current device is allowed, and means "sign this device out".</b> It is not a
    /// mistake to be guarded against: it is the only way to drop a token from the device holding it, and
    /// a client that offers a device list will offer it for the current row too (which
    /// <see cref="DeviceSummary.IsCurrent"/> exists to let it label). The response is still a <c>204</c>;
    /// the caller's very next request is a <c>401</c>. The device re-enrolls by logging in again — the
    /// row is recreated by <c>POST /auth/login</c> under the same <c>(UserId, DeviceId)</c>, and finds
    /// the account's data exactly as it left it.
    /// </para>
    /// <para>
    /// Revocation itself needs no code: <see cref="TokenRevocation"/> already fails any token whose
    /// device row is gone. All this adds is the cache eviction that makes it immediate.
    /// </para>
    /// </summary>
    private static void MapDeleteDevice(WebApplication app) =>
        app.MapDelete("/auth/devices/{deviceId}", async (
            string deviceId,
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            IMemoryCache cache,
            CancellationToken cancellationToken) =>
        {
            var device = await db.Devices.SingleOrDefaultAsync(
                d => d.UserId == currentContext.UserId && d.DeviceId == deviceId, cancellationToken);
            if (device is null)
            {
                return AuthEndpoints.Error(
                    StatusCodes.Status404NotFound, "No such device on this account.");
            }

            var deviceRowId = device.Id;
            db.Devices.Remove(device);
            await db.SaveChangesAsync(cancellationToken);

            // After the commit. The cached snapshot is keyed by the row id, and dropping it turns the
            // next request from this device into a fresh lookup that finds nothing — a 401.
            cache.Remove(TokenRevocation.CacheKey(deviceRowId));

            return Results.NoContent();
        });

    /// <summary>
    /// Signs every device out at once by rotating the user's <c>SecurityStamp</c>, which is what every
    /// outstanding token carries a copy of and is checked against on every request. The device rows
    /// survive — <b>only tokens die</b>, and each device gets back in by logging in again. Use
    /// <c>DELETE /auth/devices/{deviceId}</c> to actually detach one.
    /// <para>
    /// <b>A fresh token for the calling device comes back with the <c>200</c></b>, the same way
    /// <c>POST /auth/password</c> does it, because the stamp bump kills the caller's own token too and
    /// "log out my other devices" that logs you out as well reads as a bug.
    /// </para>
    /// <para>
    /// <b>No re-authentication.</b> Unlike a password change or an account deletion, the worst outcome
    /// here is that everyone signs in again — the action is annoying, not destructive, and demanding a
    /// password from someone who suspects a stolen token would slow down the one response that helps.
    /// </para>
    /// </summary>
    private static void MapLogoutAll(WebApplication app) =>
        app.MapPost("/auth/logout-all", async (
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            JwtTokenService tokenService,
            IMemoryCache cache,
            CancellationToken cancellationToken) =>
        {
            var user = await db.Users.SingleOrDefaultAsync(
                u => u.Id == currentContext.UserId, cancellationToken);
            var device = await db.Devices.SingleOrDefaultAsync(
                d => d.Id == currentContext.DeviceRowId && d.UserId == currentContext.UserId,
                cancellationToken);
            if (user is null || device is null)
            {
                // All but unreachable — TokenRevocation refuses a token whose user or device row is
                // gone. It exists so the nullable handling below is not wishful.
                return Results.Unauthorized();
            }

            user.SecurityStamp = Guid.NewGuid().ToString();
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            await TokenRevocation.EvictUserAsync(db, cache, user.Id, cancellationToken);

            var (token, expiresAtUtc) = tokenService.IssueToken(
                user.Id, device.Id, device.DeviceId, user.SecurityStamp);
            return Results.Ok(new LoginResponse(token, expiresAtUtc, AuthEndpoints.Summarize(user)));
        });

    /// <summary>
    /// Deletes the account and everything under it. <b>Immediate, total and irreversible</b> — hard
    /// delete, not a flag: a soft delete would put an <c>AND NotDeleted</c> term in every sync and report
    /// query for a case that arises once per account, and "your data is still on the server but we
    /// promise not to look" is not what someone asking to delete their account means. A grace period is
    /// the follow-up worth having and is a deliberate deferral, not an oversight. On a self-hosted box
    /// the person calling this <i>is</i> the operator, so no backup covers the mistake — which is why
    /// there are two independent gates before anything is dropped:
    /// <list type="number">
    /// <item><description>
    /// <b><c>401</c> — the current password is re-verified.</b> A leaked token must not be sufficient to
    /// destroy an account.
    /// </description></item>
    /// <item><description>
    /// <b><c>400</c> — <c>ConfirmEmail</c> must match this account's address</b>, case-insensitively.
    /// That is the typed-confirmation gesture, and it independently stops a client bug pointed at the
    /// wrong session from deleting the wrong account.
    /// </description></item>
    /// </list>
    /// Either failing leaves the account completely intact; nothing is written before both have passed.
    /// A credential-less pre-S6 row (see <see cref="Data.Entities.User"/>) has no password to verify and
    /// gets the same <c>409</c> naming <c>/auth/claim</c> that <c>POST /auth/password</c> gives it —
    /// refusing in the safe direction, and in practice unreachable since such a row cannot log in.
    /// <para>
    /// <b>There is deliberately no last-administrator guard here</b>, unlike on every <c>/admin/*</c>
    /// route that could remove one (see <see cref="AdminEndpoints"/>). The sole admin may delete their
    /// own account and leave the server with none. Those two rules are not in conflict: the admin
    /// guards stop one admin from stripping <i>another</i>, or from stranding a deployment full of
    /// users it cannot administer, and they are reachable by someone who should be told to promote a
    /// successor first. This is a person deleting themselves, having passed both gates above, and
    /// refusing them would be telling someone they may not leave. The state it produces is recoverable
    /// by design and by exactly the route built for it: with no admin left, <c>POST /admin/bootstrap</c>
    /// starts answering again, so the next person with the secret can promote themselves. <b>Do not
    /// "fix" this with a guard</b> — it would make the last account on a server undeletable.
    /// </para>
    /// <para>
    /// <b>Children first, one <c>ExecuteDeleteAsync</c> per table, inside one explicit transaction.</b>
    /// The FKs are <c>ON DELETE CASCADE</c>, so deleting the user row alone would in fact clear
    /// everything — the explicit order is what makes this independent of that configuration, and it is
    /// what produces the per-table counts the audit line reports. <c>ExecuteDeleteAsync</c> is one
    /// statement per table with no entity materialization, so an account with years of time entries does
    /// not have to be loaded into memory to be deleted.
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
    /// <b>The audit line carries the user id and the counts, never the email.</b> The request log in
    /// <c>Program.cs</c> deliberately records method, path and status and nothing else, and this — the
    /// one irreversible action in the API — is exactly the place tempted to widen that. The id is enough
    /// to reconcile against a backup; the address would put a person's identity in a log file that
    /// otherwise holds none.
    /// </para>
    /// </summary>
    private static void MapDeleteAccount(WebApplication app) =>
        app.MapDelete("/auth/account", async (
            // Explicit, and nullable, for two separate reasons the framework enforces: minimal APIs
            // refuse an *inferred* body on DELETE outright (the host fails to start), and a
            // non-nullable one would make an absent body the framework's own 400 rather than the
            // message below, which names both fields a caller has to send.
            [FromBody] DeleteAccountRequest? request,
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            PasswordService passwords,
            IMemoryCache cache,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (request is null)
            {
                return Results.BadRequest(
                    new { error = "A request body with the current password and the account email is required." });
            }

            var userId = currentContext.UserId;
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (user.PasswordHash is null)
            {
                return AuthEndpoints.Error(
                    StatusCodes.Status409Conflict,
                    "This account has no password yet, so it cannot be re-authenticated for deletion. " +
                    "Set one with POST /auth/claim.");
            }

            if (passwords.Verify(user.PasswordHash, request.Password ?? string.Empty) is
                PasswordVerification.Failed)
            {
                return AuthEndpoints.Error(StatusCodes.Status401Unauthorized, "Password is incorrect.");
            }

            var confirmEmail = (request.ConfirmEmail?.Trim() ?? string.Empty).ToUpperInvariant();
            if (confirmEmail.Length == 0 ||
                !string.Equals(confirmEmail, user.NormalizedEmail, StringComparison.Ordinal))
            {
                return AuthEndpoints.Error(
                    StatusCodes.Status400BadRequest,
                    "ConfirmEmail does not match this account's email address.");
            }

            // Read before the delete, not after: TokenRevocation.EvictUserAsync finds the keys to drop by
            // querying Devices, and by the time this commits there is no row left to find.
            var deviceRowIds = await db.Devices
                .Where(d => d.UserId == userId)
                .Select(d => d.Id)
                .ToListAsync(cancellationToken);

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
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

            loggerFactory.CreateLogger("AccountDeletion").LogWarning(
                "Deleted account {UserId}: {TimeEntries} time entries, {TaskItems} tasks, " +
                "{Projects} projects, {Devices} devices, {Users} user row(s).",
                userId, timeEntries, taskItems, projects, devices, users);

            return Results.NoContent();
        });
}
