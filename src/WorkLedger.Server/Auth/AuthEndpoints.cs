using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Auth;

public static class AuthEndpoints
{
    /// <summary>
    /// The rate-limiting policy every credential-taking anonymous endpoint opts into — login,
    /// registration, claim and password-reset redemption here, and the portal's anonymous credential
    /// pages (<c>Web/PortalSetup.cs</c>). Named here next to the endpoints that use it; configured in
    /// <c>Program.cs</c>.
    /// </summary>
    public const string RateLimitPolicy = "auth-credentials";

    /// <summary>
    /// The whole auth surface. Everything <i>in this file</i> except <c>/auth/whoami</c> and
    /// <c>/auth/password</c> is <c>AllowAnonymous</c> — by definition, since a caller with no account and
    /// no token is exactly who those endpoints are for. The authenticated ones are left to Program.cs's
    /// fallback policy, which is also what protects every route in <see cref="AccountEndpoints"/>: S8's
    /// device-management and account-deletion routes are mapped from here so the auth surface is
    /// registered in one call, but live in their own file because none of them takes a credential from an
    /// anonymous caller.
    /// <para>
    /// <b>The rules live in services, not here</b> (S11): <see cref="CredentialCheck"/>,
    /// <see cref="RegistrationService"/>, <see cref="PasswordResetService"/> and
    /// <see cref="AccountSelfService"/>. What stays in this file is the HTTP surface — which status code
    /// each outcome is, where the secret arrives from, and what comes back.
    /// </para>
    /// <para>
    /// <b>The secret arrives in <c>X-Bootstrap-Secret</c></b>, a header, so it never reaches the request
    /// log (see <c>Program.cs</c>). See <see cref="RegistrationService"/> for how its job has changed.
    /// </para>
    /// <para>
    /// <b>Registration, claim, password change and password reset are compatibility routes</b> (S11): the
    /// web portal is where accounts are managed now, and these stay only so existing clients and scripts
    /// keep working. See <see cref="AccountEndpoints"/> for the full list and the rule that comes with it.
    /// </para>
    /// </summary>
    public static void MapAuthEndpoints(this WebApplication app)
    {
        MapCapabilities(app);
        MapRegister(app);
        MapClaim(app);
        MapLogin(app);
        MapChangePassword(app);
        MapResetPassword(app);
        MapWhoAmI(app);
        app.MapAccountEndpoints();
        app.MapAdminEndpoints();
    }

    /// <summary>
    /// Lets a client render the truth about this server rather than discovering it from an error: hide
    /// the registration UI when registration is off, offer "Claim this server" while an unclaimed row
    /// exists, state the real password minimum, and warn before a login attempt that would lock the
    /// account. See <see cref="AuthCapabilitiesResponse"/> for why static policy is safe to serve
    /// anonymously and why this is not a remaining-attempts count. Since S11 it is also how the client
    /// finds the web portal.
    /// </summary>
    private static void MapCapabilities(WebApplication app) =>
        app.MapGet("/auth/capabilities", async (
            WorkLedgerDbContext db,
            AuthOptions auth,
            WebOptions web,
            CancellationToken cancellationToken) =>
        {
            var unclaimed = await UnclaimedUser.FindAsync(db, cancellationToken);
            return Results.Ok(new AuthCapabilitiesResponse(
                RegistrationEnabled: auth.RegistrationEnabled,
                // Was hardcoded true until S10, with a comment admitting it. Now it reports whether the
                // shared secret is actually still accepted, so a server that has moved to invite-only
                // says so instead of advertising a credential it will refuse.
                RegistrationRequiresSecret: auth.SignupSecretEnabled,
                MinPasswordLength: auth.MinPasswordLength,
                HasUnclaimedAccount: unclaimed is not null,
                MaxFailedLogins: auth.MaxFailedLogins,
                LockoutMinutes: auth.ReportedLockoutMinutes,
                // Not configurable: a server running this code always takes an invite. It is on the wire
                // so a client can tell this server from an older one without version-sniffing.
                RegistrationAcceptsInvite: true,
                // Null while the portal is off, so a client never links to pages that 404.
                PortalUrl: web.PortalUrl));
        }).AllowAnonymous();

    /// <summary>
    /// Creates a user through <see cref="RegistrationService.RegisterAsync"/>, whose doc states the gates,
    /// their order and why each refusal says what it says. On the wire: <c>403</c> registration disabled,
    /// <c>401</c> no acceptable credential (or an unknown invite token), <c>409</c> a used, expired or
    /// wrongly-addressed invite, <c>400</c> bad email or password, <c>409</c> email already taken, and
    /// <c>201</c> + <see cref="UserSummary"/> on success.
    /// <para>
    /// <b>403 rather than 404 when registration is off.</b> <c>/auth/capabilities</c> already advertises
    /// the flag, so hiding the route buys no secrecy and costs an operator a confusing session.
    /// </para>
    /// <para>
    /// <b>No token in the response.</b> Tokens are device-scoped and minted by <c>/auth/login</c>;
    /// registration is a user-level act with no device to bind one to.
    /// </para>
    /// <para>
    /// <b>A compatibility route (S11).</b> It keeps working unchanged, and <b>no new behaviour lands on
    /// it</b>: see <see cref="AccountEndpoints"/>.
    /// </para>
    /// </summary>
    private static void MapRegister(WebApplication app) =>
        app.MapPost("/auth/register", async (
            RegisterUserRequest request,
            HttpRequest httpRequest,
            RegistrationService registration,
            CancellationToken cancellationToken) =>
        {
            var result = await registration.RegisterAsync(
                request, ProvidedSecret(httpRequest), cancellationToken);

            // Results.Created would advertise a Location this API does not serve — there is no
            // GET /users/{id}, and S8 has no plans for one.
            return result is RegistrationResult.Succeeded { User: var user }
                ? Results.Json(Summarize(user), statusCode: StatusCodes.Status201Created)
                : RegistrationFailure(result);
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);

    /// <summary>
    /// Claims the pre-S6 legacy row through <see cref="RegistrationService.ClaimAsync"/>: <c>200</c> +
    /// <see cref="UserSummary"/>, <c>401</c> for a bad secret, <c>400</c> for bad input, or <c>409</c> when
    /// there is nothing claimable.
    /// <para>
    /// <b>A compatibility route (S11).</b> It keeps working unchanged, and <b>no new behaviour lands on
    /// it</b>: see <see cref="AccountEndpoints"/>.
    /// </para>
    /// </summary>
    private static void MapClaim(WebApplication app) =>
        app.MapPost("/auth/claim", async (
            RegisterUserRequest request,
            HttpRequest httpRequest,
            RegistrationService registration,
            CancellationToken cancellationToken) =>
        {
            var result = await registration.ClaimAsync(request, ProvidedSecret(httpRequest), cancellationToken);
            return result is RegistrationResult.Succeeded { User: var user }
                ? Results.Ok(Summarize(user))
                : RegistrationFailure(result);
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);

    /// <summary>
    /// <b>The only way to obtain a token</b> — S7 deleted <c>/auth/register-device</c>, whose job this
    /// absorbed. Credentials are user-level, tokens are device-level: a person signs in <i>from a
    /// device</i>, so the request carries the client's per-install <c>DeviceId</c> and what comes back is
    /// a JWT scoped to <c>(user, device)</c>. Everything downstream needs both halves — <c>SyncService</c>
    /// compares <c>OriginDeviceId</c> to decide what to echo back on a pull, and the tenancy invariant is
    /// that <c>UserId</c> comes from the token.
    /// <para>
    /// <b>Check, then enroll, then mint.</b> The password and the lockout are
    /// <see cref="CredentialCheck"/>'s, and its doc carries the rules that look wrong until you know why:
    /// <b>every failure is the same <c>401</c></b> <c>{ error = "Invalid email or password." }</c>,
    /// <b>the counter resets when the lockout window expires</b>, and <b>a <c>423</c> for an account
    /// already locked writes nothing at all</b>. The attempt that crosses the threshold also answers
    /// <c>423</c>, with a <c>Retry-After</c>.
    /// </para>
    /// <para>
    /// <b>Enrollment is a side effect of signing in.</b> The device row is upserted under this user with
    /// the idempotent semantics <c>/auth/register-device</c> had, so a second login from the same install
    /// reuses its row rather than accumulating duplicates. The same <c>DeviceId</c> presented by a
    /// <i>different</i> user is a different row under a different user and shares nothing with the first:
    /// the unique index is on <c>(UserId, DeviceId)</c>, not on <c>DeviceId</c> alone.
    /// </para>
    /// <para>
    /// One race is left exactly as <c>/auth/register-device</c> left it: two genuinely simultaneous
    /// logins from one install can both miss the existing device row, and the unique
    /// <c>(UserId, DeviceId)</c> index refuses the loser. A single install does not do that, and a retry
    /// succeeds.
    /// </para>
    /// </summary>
    private static void MapLogin(WebApplication app) =>
        app.MapPost("/auth/login", async (
            LoginRequest request,
            HttpResponse httpResponse,
            WorkLedgerDbContext db,
            CredentialCheck credentialCheck,
            JwtTokenService tokenService,
            CancellationToken cancellationToken) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.DeviceId))
            {
                return Results.BadRequest(new { error = "DeviceId is required." });
            }

            var check = await credentialCheck.CheckAsync(request.Email, request.Password, cancellationToken);
            if (check is not CredentialCheckResult.Verified { User: var user })
            {
                // The caller's next question after a 423 is "when can I try again", and Retry-After is
                // the answer to it.
                return check is CredentialCheckResult.LockedOut { UntilUtc: var until }
                    ? LockedOut(httpResponse, until, DateTime.UtcNow)
                    : InvalidCredentials();
            }

            var device = await UpsertDeviceAsync(
                db, user.Id, request.DeviceId, request.DeviceLabel, DateTime.UtcNow, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            var (token, expiresAtUtc) = tokenService.IssueToken(
                user.Id, device.Id, device.DeviceId, user.SecurityStamp);
            return Results.Ok(new LoginResponse(token, expiresAtUtc, Summarize(user)));
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);

    /// <summary>
    /// Rotates the caller's password through <see cref="AccountSelfService.ChangePasswordAsync"/>, which
    /// re-verifies the current one and bumps the stamp — see there for why. <c>401</c> on a wrong current
    /// password, <c>400</c> on a new one that fails policy, and <c>409</c> naming <c>/auth/claim</c> for the
    /// unclaimed legacy row.
    /// <para>
    /// The stamp bump signs out every device <i>including the caller's own</i>, so the <c>200</c> carries
    /// a freshly minted token for the calling device: otherwise changing your password signs you out of
    /// the app you just changed it in, which reads as a bug.
    /// </para>
    /// <para>
    /// <b>A compatibility route (S11).</b> It keeps working unchanged, and <b>no new behaviour lands on
    /// it</b>: see <see cref="AccountEndpoints"/>.
    /// </para>
    /// </summary>
    private static void MapChangePassword(WebApplication app) =>
        app.MapPost("/auth/password", async (
            ChangePasswordRequest request,
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            AccountSelfService selfService,
            JwtTokenService tokenService,
            CancellationToken cancellationToken) =>
        {
            if (request is null)
            {
                return Results.BadRequest(
                    new { error = "A request body with the current and new password is required." });
            }

            var device = await db.Devices.SingleOrDefaultAsync(
                d => d.Id == currentContext.DeviceRowId && d.UserId == currentContext.UserId, cancellationToken);
            if (device is null)
            {
                // TokenRevocation refuses a token whose user or device row is gone, so this is all but
                // unreachable — it exists so the nullable checks below are not wishful.
                return Results.Unauthorized();
            }

            var result = await selfService.ChangePasswordAsync(
                currentContext.UserId, request.CurrentPassword, request.NewPassword, cancellationToken);

            switch (result.Outcome)
            {
                case SelfServiceOutcome.Done:
                    var user = result.User!;
                    var (token, expiresAtUtc) = tokenService.IssueToken(
                        user.Id, device.Id, device.DeviceId, user.SecurityStamp);
                    return Results.Ok(new LoginResponse(token, expiresAtUtc, Summarize(user)));
                case SelfServiceOutcome.NoPassword:
                    return Error(
                        StatusCodes.Status409Conflict,
                        "This account has no password yet. Set one with POST /auth/claim.");
                case SelfServiceOutcome.WrongPassword:
                    return Error(StatusCodes.Status401Unauthorized, "Current password is incorrect.");
                case SelfServiceOutcome.InvalidPassword:
                    return Results.BadRequest(new { error = result.Error });
                default:
                    return Results.Unauthorized();
            }
        });

    /// <summary>
    /// Who this token belongs to. Carries the email and display name so a client can render the
    /// signed-in identity from the one call it already makes rather than a second one.
    /// </summary>
    private static void MapWhoAmI(WebApplication app) =>
        app.MapGet("/auth/whoami", async (
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            CancellationToken cancellationToken) =>
        {
            var user = await db.Users
                .Where(u => u.Id == currentContext.UserId)
                .Select(u => new { u.Email, u.DisplayName })
                .SingleOrDefaultAsync(cancellationToken);

            return user is null
                ? Results.Unauthorized()
                : Results.Ok(new WhoAmIResponse(
                    currentContext.UserId,
                    // Empty rather than null for the unclaimed legacy row, which has devices and a token
                    // but no email: the field is non-nullable on the wire and "" reads as "not set yet".
                    user.Email ?? string.Empty,
                    user.DisplayName,
                    currentContext.DeviceId));
        });

    /// <summary>
    /// Spends a reset token issued by <c>POST /admin/users/{id}/password-reset</c> through
    /// <see cref="PasswordResetService.RedeemAsync"/>: sets the new password, signs every device out, and
    /// returns <c>204</c>.
    /// <para>
    /// <b>Anonymous, because the whole point is a caller who cannot authenticate.</b> The token is the
    /// credential, and it is rate-limited under the same policy as login, registration and claim.
    /// </para>
    /// <para>
    /// <b>Unknown, expired and already-spent tokens are one identical <c>400</c></b> — see the service for
    /// why. A new password that fails policy is a <c>400</c> naming the rule, and leaves the token
    /// spendable.
    /// </para>
    /// <para>
    /// <b>No token comes back, unlike <c>POST /auth/password</c>.</b> That one re-mints for the calling
    /// device because the caller was already signed in on it; here there is no device to mint for — the
    /// request names none.
    /// </para>
    /// <para>
    /// <b>A compatibility route (S11).</b> It keeps working unchanged, and <b>no new behaviour lands on
    /// it</b>: see <see cref="AccountEndpoints"/>.
    /// </para>
    /// </summary>
    private static void MapResetPassword(WebApplication app) =>
        app.MapPost("/auth/password/reset", async (
            ResetPasswordRequest? request,
            PasswordResetService passwordResets,
            CancellationToken cancellationToken) =>
        {
            var result = await passwordResets.RedeemAsync(
                request?.ResetToken, request?.NewPassword, cancellationToken);

            return result switch
            {
                PasswordResetRedemption.Redeemed => Results.NoContent(),
                PasswordResetRedemption.MissingToken => Results.BadRequest(
                    new { error = "A request body with a reset token and a new password is required." }),
                PasswordResetRedemption.InvalidPassword { Error: var error } =>
                    Results.BadRequest(new { error }),
                _ => Error(StatusCodes.Status400BadRequest, PasswordResetService.InvalidTokenMessage),
            };
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);

    /// <summary>
    /// The status code and body for every way registration or claim can refuse. See
    /// <see cref="RegistrationService"/> for why each refusal is as frank, or as flat, as it is.
    /// </summary>
    private static IResult RegistrationFailure(RegistrationResult result) => result switch
    {
        RegistrationResult.Invalid { Error: var error } => Results.BadRequest(new { error }),
        RegistrationResult.Refused { Reason: var reason } => reason switch
        {
            RegistrationRefusal.Disabled =>
                Error(StatusCodes.Status403Forbidden, RegistrationService.Sentence(reason)!),
            RegistrationRefusal.InviteUsed or
            RegistrationRefusal.InviteExpired or
            RegistrationRefusal.InviteEmailMismatch or
            RegistrationRefusal.DuplicateEmail =>
                Error(StatusCodes.Status409Conflict, RegistrationService.Sentence(reason)!),
            RegistrationRefusal.NothingToClaim => Error(
                StatusCodes.Status409Conflict,
                "Nothing to claim. This server has no unclaimed account — use POST /auth/register instead."),
            _ => Results.Unauthorized(),
        },
        _ => throw new InvalidOperationException($"Not a registration failure: {result}."),
    };

    /// <summary>
    /// Creates the device row or refreshes the existing one, matching on <c>(UserId, DeviceId)</c> — the
    /// unique index — so repeated logins from one install are idempotent. A blank label leaves whatever
    /// label the device already had; <c>LastSeenUtc</c> is always bumped. Carried over verbatim from
    /// <c>/auth/register-device</c>. Not saved here: the caller commits it.
    /// </summary>
    private static async Task<Device> UpsertDeviceAsync(
        WorkLedgerDbContext db,
        int userId,
        string deviceId,
        string? label,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var device = await db.Devices.SingleOrDefaultAsync(
            d => d.UserId == userId && d.DeviceId == deviceId, cancellationToken);

        if (device is null)
        {
            device = new Device
            {
                UserId = userId,
                DeviceId = deviceId,
                Label = label,
                CreatedAt = now,
                LastSeenUtc = now,
            };
            db.Devices.Add(device);
            return device;
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            device.Label = label;
        }

        device.LastSeenUtc = now;
        return device;
    }

    /// <summary>Internal rather than private so <see cref="AccountEndpoints"/>' logout-all can answer with the
    /// same <see cref="UserSummary"/> shape login does, rather than a second copy of it that can drift.</summary>
    internal static UserSummary Summarize(User user) =>
        new(user.Id, user.Email ?? string.Empty, user.DisplayName, user.CreatedAt);

    private static IResult InvalidCredentials() =>
        Error(StatusCodes.Status401Unauthorized, "Invalid email or password.");

    /// <summary>
    /// <c>423 Locked</c> with a <c>Retry-After</c> in seconds, rounded up and never below 1 so a
    /// sub-second remainder does not advertise as "retry immediately". The body states the policy rather
    /// than the deadline, which the header already carries in the form a client can act on.
    /// </summary>
    private static IResult LockedOut(HttpResponse httpResponse, DateTime lockedUntilUtc, DateTime now)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((lockedUntilUtc - now).TotalSeconds));
        httpResponse.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Error(
            StatusCodes.Status423Locked,
            "Too many failed sign-in attempts. This account is temporarily locked; try again later.");
    }

    /// <summary>The one error body shape this API's auth surface answers with. Internal so
    /// <see cref="AccountEndpoints"/> shares it.</summary>
    internal static IResult Error(int statusCode, string message) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    /// <summary>
    /// The value of <c>X-Bootstrap-Secret</c>, or null when the header is absent. Compared by
    /// <see cref="BootstrapOptions.SecretMatches"/>, never here. Internal so <see cref="AdminEndpoints"/>
    /// reads it from the same header the same way.
    /// </summary>
    internal static string? ProvidedSecret(HttpRequest httpRequest) =>
        httpRequest.Headers.TryGetValue("X-Bootstrap-Secret", out var provided) ? provided.ToString() : null;
}
