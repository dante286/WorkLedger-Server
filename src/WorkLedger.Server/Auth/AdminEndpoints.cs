using Microsoft.AspNetCore.Mvc;
using WorkLedger.Contracts.Auth;

namespace WorkLedger.Server.Auth;

/// <summary>
/// The operator surface's JSON routes (S10): the scripting interface to everything a person running
/// this deployment for other people needs to do to an account that is not their own. The operations
/// themselves — and their guards, eviction and audit line — live in <see cref="AdminOperations"/> and
/// <see cref="InviteService"/> (S11); this file maps their outcomes to status codes.
/// <para>
/// <b>Why this exists at all.</b> While the only user <i>was</i> the operator, "an operator with shell
/// access has <c>psql</c>" was true and sufficient. It stops being either once other people have
/// accounts: the documented recovery for a forgotten password or a lockout became an operator opening a
/// shell against the volume, per incident, for someone else's account. These routes are that recovery,
/// made ordinary.
/// </para>
/// <para>
/// <b>Not a compatibility route, and not superseded by the portal (S11).</b> <c>/portal/admin</c> is the
/// operator's UI for the same operations, and it calls the same <see cref="AdminOperations"/> and
/// <see cref="InviteService"/> methods these routes do — so the guards, the eviction and the audit line
/// are one piece of code whichever surface is used. These routes remain the scripting interface and are
/// documented in the README beside the portal. Their request and response shapes are server-internal
/// (<c>Auth/AdminDtos.cs</c>), not part of <c>WorkLedger.Contracts</c>: the desktop client never calls them.
/// </para>
/// <para>
/// <b>Authority comes from the database, not the token.</b> Every route here but
/// <c>/admin/bootstrap</c> is <c>.RequireAuthorization(TokenRevocation.AdminPolicy)</c>, and that
/// policy's claim is put on the principal by <see cref="TokenRevocation"/> from the <c>IsAdmin</c> column
/// on each request — see its doc for why a year-long token must not carry a privilege. A non-admin gets
/// <c>403</c>, not <c>404</c>: these routes are documented in the README, so hiding them buys no secrecy
/// and costs an operator a confusing session.
/// </para>
/// <para>
/// <b>No-ops are <c>204</c> too.</b> Unlocking an unlocked user, suspending a suspended one, promoting an
/// admin: the operator's intent is satisfied either way, and a <c>409</c> would only make them check the
/// list first. <c>409</c> is kept for the guards — acting on yourself, or removing the last admin — and
/// its body names the fix.
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
        MapIssuePasswordReset(app);
        MapInvites(app);
    }

    /// <summary>
    /// Promotes the <b>calling</b> user to admin through <see cref="AdminOperations.BootstrapAsync"/>,
    /// only while this deployment has no admin at all. <c>204</c>; <c>401</c> for a bad
    /// <c>X-Bootstrap-Secret</c>; <c>409</c> the moment any admin exists.
    /// <para>
    /// <b>The one route in this file that is not behind the admin policy</b> — it cannot be, because it
    /// exists precisely for the state in which nobody can satisfy that policy. It still takes a token as
    /// well as the secret; the service explains why that is not a mistake.
    /// </para>
    /// </summary>
    private static void MapBootstrap(WebApplication app) =>
        app.MapPost("/admin/bootstrap", async (
            HttpRequest httpRequest,
            ICurrentContext currentContext,
            AdminOperations admin,
            CancellationToken cancellationToken) =>
        {
            var outcome = await admin.BootstrapAsync(
                currentContext.UserId, AuthEndpoints.ProvidedSecret(httpRequest), cancellationToken);

            return outcome switch
            {
                AdminOutcome.Done => Results.NoContent(),
                AdminOutcome.AdminAlreadyExists => AuthEndpoints.Error(
                    StatusCodes.Status409Conflict,
                    "This server already has an administrator. Ask them to promote you with " +
                    "POST /admin/users/{id}/admin."),
                // BadSecret, and NoSuchUser — the caller's own row being gone.
                _ => Results.Unauthorized(),
            };
        });

    /// <summary>Every user on the deployment, oldest first. See <see cref="AdminOperations.ListUsersAsync"/>.</summary>
    private static void MapListUsers(WebApplication app) =>
        app.MapGet("/admin/users", async (
            AdminOperations admin,
            CancellationToken cancellationToken) =>
            Results.Ok(await admin.ListUsersAsync(cancellationToken)))
        .RequireAuthorization(TokenRevocation.AdminPolicy);

    /// <summary>
    /// Clears a lockout through <see cref="AdminOperations.UnlockAsync"/>. <c>204</c>, and <c>204</c>
    /// again for a user who was not locked.
    /// </summary>
    private static void MapUnlockUser(WebApplication app) =>
        app.MapPost("/admin/users/{userId:int}/unlock", async (
            int userId,
            ICurrentContext currentContext,
            AdminOperations admin,
            CancellationToken cancellationToken) =>
            ToResult(await admin.UnlockAsync(currentContext.UserId, userId, cancellationToken), "unlock"))
        .RequireAuthorization(TokenRevocation.AdminPolicy);

    /// <summary>
    /// Suspends (<c>POST .../disable</c>) or restores (<c>POST .../enable</c>) an account through
    /// <see cref="AdminOperations.DisableAsync"/> and <see cref="AdminOperations.EnableAsync"/>.
    /// <c>204</c>, and <c>204</c> for a no-op in either direction. Disabling yourself, or the last
    /// admin, is a <c>409</c>.
    /// </summary>
    private static void MapSetUserDisabled(WebApplication app)
    {
        app.MapPost("/admin/users/{userId:int}/disable", async (
            int userId,
            ICurrentContext currentContext,
            AdminOperations admin,
            CancellationToken cancellationToken) =>
            ToResult(await admin.DisableAsync(currentContext.UserId, userId, cancellationToken), "suspend"))
        .RequireAuthorization(TokenRevocation.AdminPolicy);

        app.MapPost("/admin/users/{userId:int}/enable", async (
            int userId,
            ICurrentContext currentContext,
            AdminOperations admin,
            CancellationToken cancellationToken) =>
            ToResult(await admin.EnableAsync(currentContext.UserId, userId, cancellationToken), "enable"))
        .RequireAuthorization(TokenRevocation.AdminPolicy);
    }

    /// <summary>
    /// Grants (<c>POST .../admin</c>) or withdraws (<c>DELETE .../admin</c>) the operator role through
    /// <see cref="AdminOperations.PromoteAsync"/> and <see cref="AdminOperations.DemoteAsync"/>.
    /// <c>204</c>, and <c>204</c> for a no-op in either direction. <b>Demoting yourself is allowed</b> —
    /// the service says why — unless you are the last admin, which is a <c>409</c>.
    /// </summary>
    private static void MapSetUserAdmin(WebApplication app)
    {
        app.MapPost("/admin/users/{userId:int}/admin", async (
            int userId,
            ICurrentContext currentContext,
            AdminOperations admin,
            CancellationToken cancellationToken) =>
            ToResult(await admin.PromoteAsync(currentContext.UserId, userId, cancellationToken), "promote"))
        .RequireAuthorization(TokenRevocation.AdminPolicy);

        app.MapDelete("/admin/users/{userId:int}/admin", async (
            int userId,
            ICurrentContext currentContext,
            AdminOperations admin,
            CancellationToken cancellationToken) =>
            ToResult(await admin.DemoteAsync(currentContext.UserId, userId, cancellationToken), "demote"))
        .RequireAuthorization(TokenRevocation.AdminPolicy);
    }

    /// <summary>
    /// Deletes someone else's account and everything under it through
    /// <see cref="AdminOperations.DeleteUserAsync"/>. The gate is
    /// <see cref="AdminDeleteUserRequest.ConfirmEmail"/> — the service explains why it is that and not a
    /// password. On the wire: <c>204</c>; <c>409</c> for yourself (naming <c>DELETE /auth/account</c>) or
    /// the last admin; <c>400</c> for a missing body or a mismatched email; <c>404</c> for an unknown id.
    /// </summary>
    private static void MapDeleteUser(WebApplication app) =>
        app.MapDelete("/admin/users/{userId:int}", async (
            int userId,
            // Explicit and nullable for the same two reasons DELETE /auth/account gives: minimal APIs
            // refuse an inferred body on DELETE, and a non-nullable one turns an absent body into the
            // framework's own 400 instead of a message naming the field.
            [FromBody] AdminDeleteUserRequest? request,
            ICurrentContext currentContext,
            AdminOperations admin,
            CancellationToken cancellationToken) =>
        {
            // An absent body is null, which the service reports as "no confirmation offered"; a body
            // without the field is "", which is a confirmation that matches nothing.
            var confirmEmail = request is null ? null : request.ConfirmEmail ?? string.Empty;
            var outcome = await admin.DeleteUserAsync(
                currentContext.UserId, userId, confirmEmail, cancellationToken);

            return outcome switch
            {
                AdminOutcome.SelfAction => AuthEndpoints.Error(
                    StatusCodes.Status409Conflict,
                    "You cannot delete your own account from the admin routes. Use DELETE /auth/account, " +
                    "which re-verifies your password."),
                AdminOutcome.ConfirmationMissing => Results.BadRequest(
                    new { error = "A request body with the target account's email is required." }),
                AdminOutcome.ConfirmationMismatch => AuthEndpoints.Error(
                    StatusCodes.Status400BadRequest,
                    "ConfirmEmail does not match that account's email address."),
                _ => ToResult(outcome, "delete"),
            };
        }).RequireAuthorization(TokenRevocation.AdminPolicy);

    /// <summary>
    /// Mints a single-use password-reset token through
    /// <see cref="AdminOperations.IssuePasswordResetAsync"/> and returns it <b>once</b>, as a
    /// <see cref="PasswordResetTokenResponse"/>, for the admin to hand over out of band. It does not set a
    /// password; <c>POST /auth/password/reset</c> spends it. <c>404</c> for an unknown id, and <c>409</c>
    /// naming <c>/auth/claim</c> for the credential-less pre-S6 row.
    /// </summary>
    private static void MapIssuePasswordReset(WebApplication app) =>
        app.MapPost("/admin/users/{userId:int}/password-reset", async (
            int userId,
            ICurrentContext currentContext,
            AdminOperations admin,
            CancellationToken cancellationToken) =>
        {
            var (outcome, reset) = await admin.IssuePasswordResetAsync(
                currentContext.UserId, userId, cancellationToken);

            return outcome switch
            {
                AdminOutcome.Done => Results.Ok(new PasswordResetTokenResponse(reset!.Token, reset.ExpiresAtUtc)),
                AdminOutcome.NoCredentials => AuthEndpoints.Error(
                    StatusCodes.Status409Conflict,
                    "That account has no credentials yet, so there is nothing to reset. It is the " +
                    "pre-user-accounts row; claim it with POST /auth/claim."),
                _ => ToResult(outcome, "reset"),
            };
        }).RequireAuthorization(TokenRevocation.AdminPolicy);

    /// <summary>
    /// Issue, list and revoke invites (S10) through <see cref="InviteService"/>. Admin-only, like
    /// everything else in this file. <b>The raw token leaves the server exactly once</b>, in the
    /// <c>201</c> from the create call; an operator who loses one revokes it and issues another.
    /// Revoking a redeemed invite is a <c>409</c> — the service explains why it is kept.
    /// </summary>
    private static void MapInvites(WebApplication app)
    {
        app.MapPost("/admin/invites", async (
            InviteRequest? request,
            ICurrentContext currentContext,
            InviteService invites,
            CancellationToken cancellationToken) =>
        {
            var result = await invites.IssueAsync(
                currentContext.UserId, request?.Email, request?.ExpiresInDays, request?.Note, cancellationToken);

            return result switch
            {
                InviteIssue.Issued { Token: var token, Invite: var invite } => Results.Json(
                    new InviteResponse(token, invite), statusCode: StatusCodes.Status201Created),
                InviteIssue.InvalidEmail => Results.BadRequest(
                    new { error = "Email must contain a single '@' with text on both sides of it." }),
                _ => Results.BadRequest(new { error = "ExpiresInDays must be greater than zero." }),
            };
        }).RequireAuthorization(TokenRevocation.AdminPolicy);

        app.MapGet("/admin/invites", async (
            InviteService invites,
            CancellationToken cancellationToken) =>
            Results.Ok(await invites.ListAsync(cancellationToken)))
        .RequireAuthorization(TokenRevocation.AdminPolicy);

        app.MapDelete("/admin/invites/{inviteId:int}", async (
            int inviteId,
            ICurrentContext currentContext,
            InviteService invites,
            CancellationToken cancellationToken) =>
            await invites.RevokeAsync(currentContext.UserId, inviteId, cancellationToken) switch
            {
                InviteRevocation.Revoked => Results.NoContent(),
                InviteRevocation.AlreadyRedeemed => AuthEndpoints.Error(
                    StatusCodes.Status409Conflict,
                    "That invite has already been redeemed. It cannot be used again, and the record of " +
                    "it is kept deliberately; delete the account it created if that is what you meant."),
                _ => AuthEndpoints.Error(StatusCodes.Status404NotFound, "No invite with that id."),
            })
        .RequireAuthorization(TokenRevocation.AdminPolicy);
    }

    /// <summary>
    /// The status code for the outcomes every admin route shares. <paramref name="verb"/> fills in the
    /// guard messages ("Cannot {verb} the last administrator").
    /// </summary>
    private static IResult ToResult(AdminOutcome outcome, string verb) => outcome switch
    {
        AdminOutcome.Done => Results.NoContent(),
        // Unlike the report and device routes, this is not hiding anything — an admin may see every
        // user — it just has nothing to act on.
        AdminOutcome.NoSuchUser => AuthEndpoints.Error(StatusCodes.Status404NotFound, "No user with that id."),
        AdminOutcome.SelfAction => AuthEndpoints.Error(
            StatusCodes.Status409Conflict,
            $"You cannot {verb} your own account from the admin routes. These act on other users; " +
            "use the routes under /auth for your own account."),
        // Names the fix, because the caller of a self-demotion is by definition the only person who can
        // carry it out.
        AdminOutcome.LastAdmin => AuthEndpoints.Error(
            StatusCodes.Status409Conflict,
            $"Cannot {verb} the last administrator — this server would be left with none, and " +
            "POST /admin/bootstrap refuses once any account exists. Promote another user first."),
        _ => throw new InvalidOperationException($"Outcome {outcome} is not expected from '{verb}'."),
    };
}
