using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Auth;

/// <summary>
/// The JSON routes for what a signed-in person can do to their own account and to the devices attached
/// to it: list them, detach one, sign them all out, or delete the lot (S8). S6 built the user row and S7
/// gave outstanding tokens a kill switch (<see cref="TokenRevocation"/>); this is what spends that
/// mechanism. The rules live in <see cref="AccountSelfService"/> (S11); this file is the HTTP surface.
/// <para>
/// <b>Every route here is authenticated and scoped entirely by the token.</b> None of them accepts a
/// user identifier from a body or a URL — the tenancy invariant in <c>CLAUDE.md</c> — so the worst a
/// caller can do with a crafted request is address something of their own.
/// </para>
/// <para>
/// <b>S10 added an operator surface, and it did not change that.</b> <see cref="AdminEndpoints"/> does
/// take target ids, but it is a different file behind a different policy; nothing here grew a way to
/// name another user. The division is worth keeping: these four routes remain provably
/// self-scoped, which is what makes them safe to reason about in isolation. <c>GET /admin/users</c>
/// reports device <i>counts</i>; no admin route lists another user's devices, or reads any of their data.
/// </para>
/// <para>
/// <b>Every route in this file is a compatibility route (S11)</b>, as are <c>POST /auth/register</c>,
/// <c>POST /auth/claim</c>, <c>POST /auth/password</c> and <c>POST /auth/password/reset</c> in
/// <see cref="AuthEndpoints"/>. Account management moved to the web portal, which calls the same services
/// these routes do; the desktop client (from client plan 12) does no account lifecycle and only signs in.
/// These routes stay, unchanged, so client builds already in the field and operators' <c>curl</c>
/// scripts keep working — and <b>no new behaviour lands on them</b>. A new account feature is built in
/// the portal, over a service, and not mirrored here. They, and the contract records only they use
/// (<c>RegisterUserRequest</c>, <c>ChangePasswordRequest</c>, <c>DeviceSummary</c>,
/// <c>DeleteAccountRequest</c>), go at the next deliberately wire-breaking release, not before.
/// <c>POST /auth/login</c>, <c>GET /auth/capabilities</c> and <c>GET /auth/whoami</c> are not on this
/// list: they are what the client still speaks. Nor is <see cref="AdminEndpoints"/>, which remains the
/// operators' scripting interface beside the portal.
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
    /// The devices signed in to this account, from <see cref="AccountSelfService.ListDevicesAsync"/>, with
    /// the calling device marked <see cref="DeviceSummary.IsCurrent"/> so a client can render a "where am
    /// I signed in" screen.
    /// <para>
    /// <b>A compatibility route (S11).</b> It keeps working unchanged, and <b>no new behaviour lands on
    /// it</b>: see <see cref="AccountEndpoints"/>.
    /// </para>
    /// </summary>
    private static void MapListDevices(WebApplication app) =>
        app.MapGet("/auth/devices", async (
            ICurrentContext currentContext,
            AccountSelfService selfService,
            CancellationToken cancellationToken) =>
            Results.Ok(await selfService.ListDevicesAsync(
                currentContext.UserId, currentContext.DeviceId, cancellationToken)));

    /// <summary>
    /// Detaches one device from this account through <see cref="AccountSelfService.RemoveDeviceAsync"/>:
    /// <c>204</c>, or <c>404</c> for a <c>deviceId</c> that is unknown <i>or belongs to another user</i> —
    /// the same answer the report routes give for a foreign <c>SyncId</c>.
    /// <para>
    /// <b>The device's synced rows are deliberately left alone</b>, and <b>removing your own current
    /// device is allowed</b> and means "sign this device out" — both explained on the service. For the
    /// latter the response is still a <c>204</c>; the caller's very next request is a <c>401</c>, and the
    /// device re-enrolls through <c>POST /auth/login</c>.
    /// </para>
    /// <para>
    /// <b>A compatibility route (S11).</b> It keeps working unchanged, and <b>no new behaviour lands on
    /// it</b>: see <see cref="AccountEndpoints"/>.
    /// </para>
    /// </summary>
    private static void MapDeleteDevice(WebApplication app) =>
        app.MapDelete("/auth/devices/{deviceId}", async (
            string deviceId,
            ICurrentContext currentContext,
            AccountSelfService selfService,
            CancellationToken cancellationToken) =>
            await selfService.RemoveDeviceAsync(currentContext.UserId, deviceId, cancellationToken)
                ? Results.NoContent()
                : AuthEndpoints.Error(StatusCodes.Status404NotFound, "No such device on this account."));

    /// <summary>
    /// Signs every device out at once through <see cref="AccountSelfService.SignOutEverywhereAsync"/>. The
    /// device rows survive — <b>only tokens die</b>. Use <c>DELETE /auth/devices/{deviceId}</c> to
    /// actually detach one.
    /// <para>
    /// <b>A fresh token for the calling device comes back with the <c>200</c></b>, the same way
    /// <c>POST /auth/password</c> does it, because the stamp bump kills the caller's own token too and
    /// "log out my other devices" that logs you out as well reads as a bug.
    /// </para>
    /// <para>
    /// <b>A compatibility route (S11).</b> It keeps working unchanged, and <b>no new behaviour lands on
    /// it</b>: see <see cref="AccountEndpoints"/>.
    /// </para>
    /// </summary>
    private static void MapLogoutAll(WebApplication app) =>
        app.MapPost("/auth/logout-all", async (
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            AccountSelfService selfService,
            JwtTokenService tokenService,
            CancellationToken cancellationToken) =>
        {
            var device = await db.Devices.SingleOrDefaultAsync(
                d => d.Id == currentContext.DeviceRowId && d.UserId == currentContext.UserId,
                cancellationToken);
            if (device is null)
            {
                // All but unreachable — TokenRevocation refuses a token whose user or device row is
                // gone. It exists so the nullable handling below is not wishful.
                return Results.Unauthorized();
            }

            var result = await selfService.SignOutEverywhereAsync(currentContext.UserId, cancellationToken);
            if (result.User is not { } user)
            {
                return Results.Unauthorized();
            }

            var (token, expiresAtUtc) = tokenService.IssueToken(
                user.Id, device.Id, device.DeviceId, user.SecurityStamp);
            return Results.Ok(new LoginResponse(token, expiresAtUtc, AuthEndpoints.Summarize(user)));
        });

    /// <summary>
    /// Deletes the account and everything under it through
    /// <see cref="AccountSelfService.DeleteAccountAsync"/> — <b>immediate, total and irreversible</b>, and
    /// behind two independent gates stated there. On the wire: <c>401</c> for a wrong password,
    /// <c>400</c> for a <c>ConfirmEmail</c> that does not match, <c>409</c> naming <c>/auth/claim</c> for a
    /// credential-less row, and <c>204</c> once it is gone. Either gate failing leaves the account
    /// completely intact.
    /// <para>
    /// <b>There is deliberately no last-administrator guard here</b>; the service explains why, and why
    /// adding one would be a bug.
    /// </para>
    /// <para>
    /// <b>A compatibility route (S11).</b> It keeps working unchanged, and <b>no new behaviour lands on
    /// it</b>: see <see cref="AccountEndpoints"/>.
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
            AccountSelfService selfService,
            CancellationToken cancellationToken) =>
        {
            if (request is null)
            {
                return Results.BadRequest(
                    new { error = "A request body with the current password and the account email is required." });
            }

            var outcome = await selfService.DeleteAccountAsync(
                currentContext.UserId, request.Password, request.ConfirmEmail, cancellationToken);

            return outcome switch
            {
                SelfServiceOutcome.Done => Results.NoContent(),
                SelfServiceOutcome.NoPassword => AuthEndpoints.Error(
                    StatusCodes.Status409Conflict,
                    "This account has no password yet, so it cannot be re-authenticated for deletion. " +
                    "Set one with POST /auth/claim."),
                SelfServiceOutcome.WrongPassword =>
                    AuthEndpoints.Error(StatusCodes.Status401Unauthorized, "Password is incorrect."),
                SelfServiceOutcome.ConfirmationMismatch => AuthEndpoints.Error(
                    StatusCodes.Status400BadRequest,
                    "ConfirmEmail does not match this account's email address."),
                _ => Results.Unauthorized(),
            };
        });
}
