using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Web;

/// <summary>
/// How a browser is signed in to the portal (S11): a <b>user-level, short-lived cookie</b>, validated on
/// every request against the same user row that already carries <c>SecurityStamp</c>, <c>IsAdmin</c> and
/// <c>DisabledAtUtc</c>. It needs no <see cref="Device"/> row — a browser is not a device, and making it
/// one would put a year-long bearer token where any script on the page could read it and a "Chrome on
/// someone's laptop" row in every user's device list.
/// <para>
/// <b>Kept apart from the bearer scheme by route, never merged.</b> The default scheme stays JwtBearer,
/// so the JSON API's fallback and admin policies never consult this cookie: a cookie that could reach
/// <c>/sync</c> or <c>/admin</c> would expose the JSON API to cross-site request forgery, since nothing
/// there checks antiforgery tokens. The portal's policies (<see cref="UserPolicy"/>,
/// <see cref="AdminPolicy"/>) authenticate with this scheme only, so a bearer token never reaches a
/// portal page either — it would bypass the portal's antiforgery and session rules. A portal page with
/// neither policy nor <c>AllowAnonymous</c> falls to the bearer fallback policy and fails closed.
/// </para>
/// <para>
/// <b>The cookie carries <c>user_id</c> and <c>stamp</c>, and never an admin claim</b> — for the reason
/// <see cref="TokenRevocation"/> keeps it out of the JWT: it would outlive a demotion.
/// <see cref="ValidatePrincipalAsync"/> puts it on the principal per request from the database, exactly
/// as the bearer path does.
/// </para>
/// </summary>
internal static class PortalSession
{
    public const string Scheme = "Portal";

    /// <summary>
    /// The <c>__Host-</c> prefix makes the browser insist on <c>Secure</c>, <c>Path=/</c> and no
    /// <c>Domain</c>, so the cookie cannot be set from, or scoped to, anything but this exact origin.
    /// </summary>
    public const string CookieName = "__Host-WorkLedger";

    public const string UserPolicy = "PortalUser";
    public const string AdminPolicy = "PortalAdmin";

    /// <summary>The same claim name the bearer token uses, so portal code and JSON code read the user id alike.</summary>
    public const string UserIdClaim = "user_id";

    public const string LoginPath = WebOptions.PortalPath + "/login";
    public const string LogoutPath = WebOptions.PortalPath + "/logout";
    public const string DeniedPath = WebOptions.PortalPath + "/denied";

    /// <summary>
    /// Signs <paramref name="user"/> in to the portal. <b>Session-only</b> (not persistent): the browser
    /// drops the cookie when it closes, and <c>Web:SessionHours</c> of inactivity ends it before that.
    /// Touches no <see cref="Device"/> row.
    /// </summary>
    public static Task SignInAsync(HttpContext httpContext, User user) =>
        httpContext.SignInAsync(Scheme, CreatePrincipal(user), new AuthenticationProperties { IsPersistent = false });

    /// <summary>The id of the portal user on <paramref name="principal"/>, which a portal policy has authenticated.</summary>
    public static int UserId(ClaimsPrincipal principal) =>
        int.Parse(
            principal.FindFirstValue(UserIdClaim)
                ?? throw new InvalidOperationException("No portal user on the current request."),
            CultureInfo.InvariantCulture);

    private static ClaimsPrincipal CreatePrincipal(User user) =>
        new(new ClaimsIdentity(
            [
                new Claim(UserIdClaim, user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(TokenRevocation.StampClaim, user.SecurityStamp),
            ],
            Scheme));

    /// <summary>
    /// Runs on every request that authenticates with this scheme. Rejects the session — and deletes the
    /// cookie — when the user is gone, suspended, or their stamp has moved since sign-in; otherwise
    /// applies the admin claim from the row.
    /// <para>
    /// The row is read through <see cref="TokenRevocation.ResolveUserAsync"/>, cached under
    /// <c>user:{id}</c> for <c>Auth:StampCacheSeconds</c>. Every path that revokes already calls
    /// <see cref="TokenRevocation.EvictUserAsync"/> after committing, and that evicts this key too — which
    /// is what makes a password change, a sign-out-everywhere, a suspension or a demotion end a portal
    /// session on its next request, not after the TTL.
    /// </para>
    /// <para>
    /// <b>Changing the principal here is not persisted.</b> The cookie handler takes its sliding-refresh
    /// copy of the ticket before this event runs, precisely so per-request additions like the admin claim
    /// never make it back into the cookie.
    /// </para>
    /// </summary>
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var claimedStamp = principal?.FindFirstValue(TokenRevocation.StampClaim);
        if (principal is null ||
            string.IsNullOrEmpty(claimedStamp) ||
            !int.TryParse(principal.FindFirstValue(UserIdClaim), CultureInfo.InvariantCulture, out var userId))
        {
            await RejectAsync(context);
            return;
        }

        var services = context.HttpContext.RequestServices;
        var snapshot = await TokenRevocation.ResolveUserAsync(
            services.GetRequiredService<WorkLedgerDbContext>(),
            services.GetRequiredService<IMemoryCache>(),
            services.GetRequiredService<AuthOptions>(),
            userId,
            context.HttpContext.RequestAborted);

        if (snapshot is null ||
            snapshot.IsDisabled ||
            !string.Equals(snapshot.SecurityStamp, claimedStamp, StringComparison.Ordinal))
        {
            await RejectAsync(context);
            return;
        }

        TokenRevocation.ApplyAdminClaim(principal, snapshot.IsAdmin);
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(Scheme);
    }

    /// <summary>
    /// The cookie handler's redirects (to sign-in, to access-denied, back to a return URL) are absolute by
    /// default, built from the request's scheme and <c>Host</c> header. Behind a TLS-terminating proxy
    /// the scheme is <c>http</c> — deliberately, since <c>X-Forwarded-Proto</c> is not trusted (see
    /// <c>Program.cs</c>) — and the host is whatever the caller sent. So every one of them is sent
    /// host-relative instead, which the browser resolves against the origin it is actually on. That also
    /// means a return URL can never leave this origin.
    /// </summary>
    public static Task RedirectRelativeAsync(RedirectContext<CookieAuthenticationOptions> context)
    {
        // The scheme check is not decoration: on Unix a bare "/path" parses as an absolute file:// URI.
        var target = Uri.TryCreate(context.RedirectUri, UriKind.Absolute, out var absolute) &&
                     absolute.Scheme is "http" or "https"
            ? absolute.PathAndQuery
            : context.RedirectUri;
        context.Response.Redirect(target);
        return Task.CompletedTask;
    }
}
