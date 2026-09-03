using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Auth;

public static class AuthEndpoints
{
    /// <summary>
    /// The whole auth surface. Everything here except <c>/auth/whoami</c> is
    /// <c>AllowAnonymous</c> — by definition, since a caller with no account and no token is exactly who
    /// these endpoints are for — and everything anonymous is instead gated on the shared
    /// <c>X-Bootstrap-Secret</c>. <c>/auth/whoami</c> is left to Program.cs's fallback policy.
    /// <para>
    /// <b>The secret's job moved.</b> In S2 it gated device bootstrap; from S6 it gates signup. The
    /// configuration key and the header name are deliberately unchanged, so an existing deployment keeps
    /// its posture and its operator changes nothing in <c>.env</c>.
    /// </para>
    /// </summary>
    public static void MapAuthEndpoints(this WebApplication app)
    {
        MapCapabilities(app);
        MapRegister(app);
        MapClaim(app);
        MapRegisterDevice(app);

        app.MapGet("/auth/whoami", (ICurrentContext currentContext) =>
            Results.Ok(new WhoAmIResponse(currentContext.UserId, currentContext.DeviceId)));
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
                LockoutMinutes: auth.LockoutMinutes));
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
    /// <b>No token in the response.</b> Tokens are device-scoped and minted by login (S7); registration
    /// is a user-level act with no device to bind one to.
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
            // NOTE THE ASYMMETRY, AND DO NOT "FIX" IT. Login (S7) must answer a wrong email and a wrong
            // password identically, because it is reachable with no secret at all. Registration being
            // frank about a duplicate is not a precedent for login being frank about anything.
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
        }).AllowAnonymous();

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
    /// <b>SecurityStamp is not bumped here.</b> The stamp revokes outstanding tokens (S7), and the
    /// legacy tenant's outstanding tokens are its own working devices — the exact thing claiming exists
    /// to preserve. Attaching a first password is not a credential rotation.
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
        }).AllowAnonymous();

    /// <summary>
    /// <b>Deleted by S7</b>, which replaces it with <c>POST /auth/login</c>. Until then it is the only
    /// way to get a token, so it stays alive and keeps binding to the single user.
    /// <para>
    /// The one addition is the fence: once <b>more than one</b> user exists it returns <c>410 Gone</c>
    /// rather than attaching the caller's device to whichever account happens to be oldest. That bug is
    /// invisible until someone else's time entries turn up on your laptop, which is far too late.
    /// </para>
    /// <para>
    /// A database with <b>zero</b> users is a different failure and gets its own <c>409</c>: nothing is
    /// gone, there is simply no account to enroll against yet.
    /// </para>
    /// </summary>
    private static void MapRegisterDevice(WebApplication app) =>
        app.MapPost("/auth/register-device", async (
            RegisterDeviceRequest request,
            HttpRequest httpRequest,
            WorkLedgerDbContext db,
            JwtTokenService tokenService,
            BootstrapOptions bootstrap,
            CancellationToken cancellationToken) =>
        {
            if (!SecretMatches(httpRequest, bootstrap))
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.DeviceId))
            {
                return Results.BadRequest(new { error = "DeviceId is required." });
            }

            var users = await db.Users.OrderBy(u => u.Id).Take(2).ToListAsync(cancellationToken);
            if (users.Count > 1)
            {
                return Error(
                    StatusCodes.Status410Gone,
                    "This server has more than one user, so /auth/register-device can no longer tell which " +
                    "account to enroll a device against. Use POST /auth/login.");
            }

            if (users.Count == 0)
            {
                return Error(
                    StatusCodes.Status409Conflict,
                    "This server has no users yet. Create one with POST /auth/register.");
            }

            var user = users[0];
            var device = await db.Devices.SingleOrDefaultAsync(
                d => d.UserId == user.Id && d.DeviceId == request.DeviceId, cancellationToken);
            var now = DateTime.UtcNow;
            if (device is null)
            {
                device = new Device { UserId = user.Id, DeviceId = request.DeviceId, Label = request.Label, LastSeenUtc = now };
                db.Devices.Add(device);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(request.Label))
                {
                    device.Label = request.Label;
                }

                device.LastSeenUtc = now;
            }

            await db.SaveChangesAsync(cancellationToken);

            var (token, expiresAtUtc) = tokenService.IssueToken(user.Id, device.Id, device.DeviceId);
            return Results.Ok(new RegisterDeviceResponse(token, expiresAtUtc));
        }).AllowAnonymous();

    private const string DuplicateEmail = "An account with that email already exists.";

    private static UserSummary Summarize(User user) =>
        new(user.Id, user.Email ?? string.Empty, user.DisplayName, user.CreatedAt);

    private static IResult Error(int statusCode, string message) =>
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
