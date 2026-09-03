using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Auth;

public static class AuthEndpoints
{
    /// <summary>
    /// The rate-limiting policy every credential-taking anonymous endpoint opts into — login,
    /// registration and claim. Named here next to the endpoints that use it; configured in
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
    /// <b>The secret's job moved, twice.</b> In S2 <c>X-Bootstrap-Secret</c> gated device bootstrap and
    /// was therefore a token-issuing credential; from S6 it gates signup; from S7, with
    /// <c>/auth/register-device</c> gone, it can no longer mint a token at all — only a password can.
    /// That is a strict security improvement, and the configuration key and header name are deliberately
    /// unchanged so an existing deployment keeps its posture and its operator changes nothing in
    /// <c>.env</c>.
    /// </para>
    /// </summary>
    public static void MapAuthEndpoints(this WebApplication app)
    {
        MapCapabilities(app);
        MapRegister(app);
        MapClaim(app);
        MapLogin(app);
        MapChangePassword(app);
        MapWhoAmI(app);
        app.MapAccountEndpoints();
    }

    /// <summary>
    /// Lets a client render the truth about this server rather than discovering it from an error: hide
    /// the registration UI when registration is off, offer "Claim this server" while an unclaimed row
    /// exists, state the real password minimum, and warn before a login attempt that would lock the
    /// account. See <see cref="AuthCapabilitiesResponse"/> for why static policy is safe to serve
    /// anonymously and why this is not a remaining-attempts count.
    /// </summary>
    private static void MapCapabilities(WebApplication app) =>
        app.MapGet("/auth/capabilities", async (
            WorkLedgerDbContext db,
            AuthOptions auth,
            CancellationToken cancellationToken) =>
        {
            var unclaimed = await UnclaimedUser.FindAsync(db, cancellationToken);
            return Results.Ok(new AuthCapabilitiesResponse(
                RegistrationEnabled: auth.RegistrationEnabled,
                // Always true today: Bootstrap:Secret is a startup requirement, so there is no
                // configuration in which signup is open. It is reported rather than hardcoded client-side
                // so an unauthenticated signup mode stays a server-side decision.
                RegistrationRequiresSecret: true,
                MinPasswordLength: auth.MinPasswordLength,
                HasUnclaimedAccount: unclaimed is not null,
                MaxFailedLogins: auth.MaxFailedLogins,
                LockoutMinutes: auth.ReportedLockoutMinutes));
        }).AllowAnonymous();

    /// <summary>
    /// Creates a user. Four gates in a fixed order, each with its own distinct failure, because an
    /// operator debugging a rejected signup needs to know which rule stopped it:
    /// <c>403</c> registration disabled, <c>401</c> bad secret, <c>400</c> bad email or password,
    /// <c>409</c> email already taken.
    /// <para>
    /// <b>403 rather than 404 when registration is off.</b> <c>/auth/capabilities</c> already advertises
    /// the flag, so hiding the route buys no secrecy and costs an operator a confusing session.
    /// </para>
    /// <para>
    /// <b>No token in the response.</b> Tokens are device-scoped and minted by <c>/auth/login</c>;
    /// registration is a user-level act with no device to bind one to.
    /// </para>
    /// </summary>
    private static void MapRegister(WebApplication app) =>
        app.MapPost("/auth/register", async (
            RegisterUserRequest request,
            HttpRequest httpRequest,
            WorkLedgerDbContext db,
            AuthOptions auth,
            BootstrapOptions bootstrap,
            PasswordService passwords,
            CancellationToken cancellationToken) =>
        {
            if (!auth.RegistrationEnabled)
            {
                return Error(StatusCodes.Status403Forbidden, "Registration is disabled on this server.");
            }

            if (!SecretMatches(httpRequest, bootstrap))
            {
                return Results.Unauthorized();
            }

            if (!Credentials.TryValidate(request, auth, out var credentials, out var validationError))
            {
                return Results.BadRequest(new { error = validationError });
            }

            // This 409 does enumerate accounts, which is an accepted tradeoff behind a shared secret on
            // a self-hosted box: the honest error is worth far more than the leak costs when the caller
            // already had to know the operator's signup secret to get here.
            //
            // NOTE THE ASYMMETRY, AND DO NOT "FIX" IT. Login answers a wrong email and a wrong password
            // identically, because it is reachable with no secret at all. Registration being frank about
            // a duplicate is not a precedent for login being frank about anything.
            if (await db.Users.AnyAsync(u => u.NormalizedEmail == credentials.NormalizedEmail, cancellationToken))
            {
                return Error(StatusCodes.Status409Conflict, DuplicateEmail);
            }

            var now = DateTime.UtcNow;
            var user = new User
            {
                Email = credentials.Email,
                NormalizedEmail = credentials.NormalizedEmail,
                PasswordHash = passwords.Hash(credentials.Password),
                DisplayName = credentials.DisplayName,
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
                // Two simultaneous signups for one address both clear the AnyAsync check above; the
                // partial unique index on NormalizedEmail is what actually settles it. Report the loser
                // as the conflict it is rather than as a 500.
                return Error(StatusCodes.Status409Conflict, DuplicateEmail);
            }

            // Results.Created would advertise a Location this API does not serve — there is no
            // GET /users/{id}, and S8 has no plans for one.
            return Results.Json(Summarize(user), statusCode: StatusCodes.Status201Created);
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);

    /// <summary>
    /// The upgrade path for a deployment that already has the S2-seeded account and real data: attaches
    /// email and password to <b>that row</b>, in place, so every project, task, time entry and enrolled
    /// device carries over untouched. <c>200</c> + <see cref="UserSummary"/>, or <c>409</c> when there is
    /// nothing claimable — see <see cref="UnclaimedUser"/> for what that means.
    /// <para>
    /// <b>Not subject to <c>Auth:RegistrationEnabled</c>.</b> An operator who switched registration off
    /// must still be able to claim their own existing data; the two flags answer different questions.
    /// </para>
    /// <para>
    /// <b>SecurityStamp is not bumped here.</b> The stamp revokes outstanding tokens, and attaching a
    /// first password is not a credential rotation. (Those pre-S7 tokens carry no <c>stamp</c> claim and
    /// are refused on their own account — see <see cref="TokenRevocation"/>. What claiming preserves is
    /// the row, its data and its enrolled devices; every device then signs in once.)
    /// </para>
    /// </summary>
    private static void MapClaim(WebApplication app) =>
        app.MapPost("/auth/claim", async (
            RegisterUserRequest request,
            HttpRequest httpRequest,
            WorkLedgerDbContext db,
            AuthOptions auth,
            BootstrapOptions bootstrap,
            PasswordService passwords,
            CancellationToken cancellationToken) =>
        {
            if (!SecretMatches(httpRequest, bootstrap))
            {
                return Results.Unauthorized();
            }

            if (!Credentials.TryValidate(request, auth, out var credentials, out var validationError))
            {
                return Results.BadRequest(new { error = validationError });
            }

            var user = await UnclaimedUser.FindAsync(db, cancellationToken);
            if (user is null)
            {
                return Error(
                    StatusCodes.Status409Conflict,
                    "Nothing to claim. This server has no unclaimed account — use POST /auth/register instead.");
            }

            user.Email = credentials.Email;
            user.NormalizedEmail = credentials.NormalizedEmail;
            user.PasswordHash = passwords.Hash(credentials.Password);
            user.DisplayName = credentials.DisplayName ?? user.DisplayName;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(Summarize(user));
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);

    /// <summary>
    /// <b>The only way to obtain a token</b> — S7 deleted <c>/auth/register-device</c>, whose job this
    /// absorbed. Credentials are user-level, tokens are device-level: a person signs in <i>from a
    /// device</i>, so the request carries the client's per-install <c>DeviceId</c> and what comes back is
    /// a JWT scoped to <c>(user, device)</c>. Everything downstream needs both halves — <c>SyncService</c>
    /// compares <c>OriginDeviceId</c> to decide what to echo back on a pull, and the tenancy invariant is
    /// that <c>UserId</c> comes from the token.
    /// <para>
    /// <b>Enrollment is a side effect of signing in.</b> The device row is upserted under this user with
    /// the idempotent semantics <c>/auth/register-device</c> had, so a second login from the same install
    /// reuses its row rather than accumulating duplicates. The same <c>DeviceId</c> presented by a
    /// <i>different</i> user is a different row under a different user and shares nothing with the first:
    /// the unique index is on <c>(UserId, DeviceId)</c>, not on <c>DeviceId</c> alone.
    /// </para>
    /// <para>
    /// <b>Every failure is the same 401, deliberately.</b> Unknown email, wrong password and a
    /// credential-less unclaimed row (see <see cref="User"/>) all answer
    /// <c>{ error = "Invalid email or password." }</c>. This endpoint is reachable by anyone who has the
    /// URL, so a distinguishable "no such account" would make it an account-enumeration oracle. It reads
    /// as inconsistent next to registration's honest <c>409</c> on a duplicate email — that asymmetry is
    /// intentional and is explained where the 409 is raised: registration sits behind the operator's
    /// shared secret, and this does not.
    /// </para>
    /// <para>
    /// <b>The three lockout counter rules.</b> The first is obvious; the other two are what a naive
    /// implementation gets wrong.
    /// <list type="number">
    /// <item><description>
    /// <b>Counters reset on success.</b> A successful login zeroes <c>FailedLoginCount</c> and clears
    /// <c>LockoutEndUtc</c>.
    /// </description></item>
    /// <item><description>
    /// <b>The counter also resets when the lockout window expires</b>, before the attempt is evaluated.
    /// Without this, a successful login is the only thing that ever clears the count, so after the first
    /// lockout the count stays at the threshold forever: the window lifts, the user's next single typo is
    /// compared against a counter already at the limit, and the account re-locks immediately. It is then
    /// permanently on one-typo-per-<c>Auth:LockoutMinutes</c>, escapable only by a successful login —
    /// exactly what a user who has forgotten which password they used cannot produce.
    /// </description></item>
    /// <item><description>
    /// <b>A 423 writes nothing at all</b> — no increment, no re-stamp. The naive
    /// <c>LockoutEndUtc = now + LockoutMinutes</c> on every refusal lets anything that polls this
    /// endpoint hold the account locked <i>indefinitely</i>: a buggy client, a script, or anyone who
    /// knows the email address. On a self-hosted box the locked-out user <i>is</i> the operator, the
    /// lockout refuses their correct password, and the only recovery is a <c>psql</c> <c>UPDATE</c>
    /// against the volume. The window is computed once, at the moment the threshold is crossed, and only
    /// read thereafter.
    /// </description></item>
    /// </list>
    /// Known tradeoff even with all three in place: on a single-user instance, someone who knows the
    /// email can keep the owner locked out <c>Auth:LockoutMinutes</c> at a time by burning the counter
    /// down again. That is accepted for a self-hosted app — the alternative, no lockout at all, is worse
    /// — and rule 3 is what keeps it <i>at a time</i> rather than permanent.
    /// </para>
    /// <para>
    /// The lockout is checked <b>before</b> the hash is verified, so a locked account burns no PBKDF2
    /// cycles, and the correct password is refused for as long as the window lasts.
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
            AuthOptions auth,
            PasswordService passwords,
            JwtTokenService tokenService,
            CancellationToken cancellationToken) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.DeviceId))
            {
                return Results.BadRequest(new { error = "DeviceId is required." });
            }

            var normalizedEmail = (request.Email?.Trim() ?? string.Empty).ToUpperInvariant();
            var user = normalizedEmail.Length == 0
                ? null
                : await db.Users.SingleOrDefaultAsync(
                    u => u.NormalizedEmail == normalizedEmail, cancellationToken);

            // One condition, two very different causes — no such user, and an unclaimed row with no
            // password to check — and deliberately the same answer for both.
            if (user?.PasswordHash is null)
            {
                return InvalidCredentials();
            }

            var now = DateTime.UtcNow;

            // Rule 2: an expired window is cleared before the attempt is evaluated, never after.
            if (user.LockoutEndUtc is { } expired && expired <= now)
            {
                user.FailedLoginCount = 0;
                user.LockoutEndUtc = null;
            }

            // Rule 3: a pure read of state. Reached only when rule 2 did not clear the window, and it
            // writes nothing — not the counter, not the window.
            if (auth.LockoutEnabled && user.LockoutEndUtc is { } lockedUntil)
            {
                return LockedOut(httpResponse, lockedUntil, now);
            }

            var verification = passwords.Verify(user.PasswordHash, request.Password ?? string.Empty);
            if (verification is PasswordVerification.Failed)
            {
                user.FailedLoginCount++;
                if (auth.LockoutEnabled && user.FailedLoginCount >= auth.MaxFailedLogins)
                {
                    user.LockoutEndUtc = now + auth.LockoutWindow;
                }

                var justLocked = user.LockoutEndUtc;
                user.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);

                // The attempt that crosses the threshold answers 423 rather than 401: the caller's next
                // question is "when can I try again", and Retry-After is the answer to it.
                return justLocked is { } until ? LockedOut(httpResponse, until, now) : InvalidCredentials();
            }

            // Rehash on the way through. The whole point of a versioned hasher is that an
            // iteration-count bump is detectable rather than a silent lockout years from now, and acting
            // on it costs one column on a login that was already writing.
            if (verification is PasswordVerification.RehashNeeded)
            {
                user.PasswordHash = passwords.Hash(request.Password!);
            }

            user.FailedLoginCount = 0;
            user.LockoutEndUtc = null;
            user.LastLoginUtc = now;
            user.UpdatedAt = now;

            var device = await UpsertDeviceAsync(
                db, user.Id, request.DeviceId, request.DeviceLabel, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            var (token, expiresAtUtc) = tokenService.IssueToken(
                user.Id, device.Id, device.DeviceId, user.SecurityStamp);
            return Results.Ok(new LoginResponse(token, expiresAtUtc, Summarize(user)));
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);

    /// <summary>
    /// Rotates the caller's password. <b>The current password is re-verified even though the caller
    /// already holds a valid token</b> (<c>401</c> on mismatch): a stolen token must not be enough to
    /// take the account over. The new password goes through the same policy registration applies
    /// (<c>400</c>), from the same accessor, so the two cannot drift.
    /// <para>
    /// Bumping <c>SecurityStamp</c> is what makes this a real rotation: every outstanding token dies, on
    /// every device — <i>including the caller's own</i>. So the response carries a freshly minted token
    /// for the calling device, because otherwise changing your password signs you out of the app you
    /// just changed it in, which reads as a bug. The other devices' cache entries are evicted directly
    /// rather than left to expire, so their sign-out is immediate rather than lagging
    /// <c>Auth:StampCacheSeconds</c> behind.
    /// </para>
    /// <para>
    /// The unclaimed legacy row (a null <c>PasswordHash</c>) gets a <c>409</c> naming <c>/auth/claim</c>
    /// rather than a <c>401</c>: there is no current password to mismatch, the caller is already
    /// authenticated so there is nothing to hide, and a 401 would send an operator hunting for a
    /// password that never existed.
    /// </para>
    /// </summary>
    private static void MapChangePassword(WebApplication app) =>
        app.MapPost("/auth/password", async (
            ChangePasswordRequest request,
            ICurrentContext currentContext,
            WorkLedgerDbContext db,
            AuthOptions auth,
            PasswordService passwords,
            JwtTokenService tokenService,
            IMemoryCache cache,
            CancellationToken cancellationToken) =>
        {
            if (request is null)
            {
                return Results.BadRequest(
                    new { error = "A request body with the current and new password is required." });
            }

            var user = await db.Users.SingleOrDefaultAsync(
                u => u.Id == currentContext.UserId, cancellationToken);
            var device = await db.Devices.SingleOrDefaultAsync(
                d => d.Id == currentContext.DeviceRowId && d.UserId == currentContext.UserId, cancellationToken);
            if (user is null || device is null)
            {
                // TokenRevocation refuses a token whose user or device row is gone, so this is all but
                // unreachable — it exists so the nullable checks below are not wishful.
                return Results.Unauthorized();
            }

            if (user.PasswordHash is null)
            {
                return Error(
                    StatusCodes.Status409Conflict,
                    "This account has no password yet. Set one with POST /auth/claim.");
            }

            if (passwords.Verify(user.PasswordHash, request.CurrentPassword ?? string.Empty) is
                PasswordVerification.Failed)
            {
                return Error(StatusCodes.Status401Unauthorized, "Current password is incorrect.");
            }

            if (!Credentials.TryValidatePassword(request.NewPassword, auth, out var validationError))
            {
                return Results.BadRequest(new { error = validationError });
            }

            user.PasswordHash = passwords.Hash(request.NewPassword ?? string.Empty);
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.FailedLoginCount = 0;
            user.LockoutEndUtc = null;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            // After the commit, never before: evicting first would only let a request already in flight
            // re-cache the stamp this call just replaced.
            await TokenRevocation.EvictUserAsync(db, cache, user.Id, cancellationToken);

            var (token, expiresAtUtc) = tokenService.IssueToken(
                user.Id, device.Id, device.DeviceId, user.SecurityStamp);
            return Results.Ok(new LoginResponse(token, expiresAtUtc, Summarize(user)));
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

    private const string DuplicateEmail = "An account with that email already exists.";

    /// <summary>
    /// Creates the device row or refreshes the existing one, matching on <c>(UserId, DeviceId)</c> — the
    /// unique index — so repeated logins from one install are idempotent. A blank label leaves whatever
    /// label the device already had; <c>LastSeenUtc</c> is always bumped. Carried over verbatim from
    /// <c>/auth/register-device</c>. Not saved here: the caller commits it with the user's counters.
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
    /// The only unique index on <c>Users</c> is the partial one on <c>NormalizedEmail</c>, so a unique
    /// violation from a user insert can only be a duplicate email. Matching on the SqlState rather than
    /// on the message keeps this independent of Postgres's error wording and of the locale it renders in.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Constant-time comparison, exactly as S2's <c>/auth/register-device</c> did it: a byte-by-byte
    /// early-exit compare would leak the secret's prefix to a caller willing to time enough requests.
    /// </summary>
    private static bool SecretMatches(HttpRequest httpRequest, BootstrapOptions bootstrap) =>
        httpRequest.Headers.TryGetValue("X-Bootstrap-Secret", out var provided) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided.ToString()),
            Encoding.UTF8.GetBytes(bootstrap.Secret));
}
