using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Web;

/// <summary>
/// Wires the web portal (S11) into the host: Razor Pages under <c>/portal</c>, the cookie options, the
/// persisted Data Protection key ring, and the response headers portal pages carry.
/// <para>
/// <b>Why services are registered unconditionally and only the pipeline is gated.</b>
/// <c>WebApplicationFactory</c> applies its configuration only when <c>Build()</c> runs (see the note at
/// the top of <c>Program.cs</c>), so nothing registered before then can depend on <c>Web:Enabled</c>
/// without the tests seeing a different app from production. So the registrations here are inert on
/// their own — a cookie scheme nothing authenticates with, Razor Pages services with no page mapped — and
/// everything with an effect on a request is either decided after <c>Build()</c>
/// (<see cref="UsePortalHeaders"/>, <see cref="MapPortal"/>) or resolved lazily from
/// <see cref="WebOptions"/> (the key ring). With the portal off, no request reaches any of it.
/// </para>
/// </summary>
internal static class PortalSetup
{
    /// <summary>
    /// Separates the key ring from any other application sharing the table or the machine, and — the
    /// reason it is pinned rather than left to default — keeps keys valid when the content root differs
    /// between two images, since the default discriminator is derived from that path.
    /// </summary>
    private const string DataProtectionApplicationName = "WorkLedger";

    /// <summary>
    /// <c>'self'</c> for everything, because every script and stylesheet is a static file served from
    /// here: <b>no inline script, so no <c>unsafe-inline</c></b>. <c>form-action 'self'</c> stops an
    /// injected form posting a password elsewhere; <c>frame-ancestors 'none'</c> stops the portal being
    /// framed for clickjacking.
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'; object-src 'none'";

    /// <summary>
    /// The pages that take a credential from someone who may not have a session — a password, the
    /// operator's secret, an invite or a reset token. Each is anonymous and rate-limited.
    /// </summary>
    private static readonly string[] CredentialPages =
        ["/Portal/Login", "/Portal/Setup", "/Portal/Register", "/Portal/Reset"];

    /// <summary>Every page that works without a session: the credential pages, plus sign-out and access-denied.</summary>
    private static readonly string[] AnonymousPages = [.. CredentialPages, "/Portal/Logout", "/Portal/Denied"];

    public static void AddPortal(this IServiceCollection services)
    {
        services.AddSingleton(sp => WebOptions.Resolve(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<PortalLinks>();

        services.AddRazorPages(options =>
        {
            // Every page under /Portal needs a portal session unless it says otherwise, and the admin
            // folder needs the admin role too. A page outside these conventions would fall to the bearer
            // fallback policy and fail closed, which is the direction to fail in.
            options.Conventions.AuthorizeFolder("/Portal", PortalSession.UserPolicy);
            options.Conventions.AuthorizeFolder("/Portal/Admin", PortalSession.AdminPolicy);
            foreach (var page in AnonymousPages)
            {
                options.Conventions.AllowAnonymousToPage(page);
            }

            // The same per-IP bucket as POST /auth/login: one box, one budget for guessing passwords,
            // secrets and tokens, whichever form the guesses arrive through. The policy never limits a
            // GET (see Program.cs), so rendering a form costs nothing.
            foreach (var page in CredentialPages)
            {
                options.Conventions.AddPageApplicationModelConvention(page, model =>
                    model.EndpointMetadata.Add(new EnableRateLimitingAttribute(AuthEndpoints.RateLimitPolicy)));
            }
        });

        // Pages live in Pages/Portal/, so generated links would otherwise read /Portal/Login.
        services.Configure<RouteOptions>(options => options.LowercaseUrls = true);

        services.AddOptions<CookieAuthenticationOptions>(PortalSession.Scheme)
            .Configure<WebOptions>((options, web) =>
            {
                options.Cookie.Name = PortalSession.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.Path = "/";
                options.Cookie.SameSite = SameSiteMode.Lax;

                // Always, rather than "when the request is HTTPS". Behind a TLS-terminating proxy this
                // process sees plain http and deliberately does not trust X-Forwarded-Proto to say
                // otherwise (Program.cs explains the redirect loops that invites). It does not need to:
                // the browser accepts a Secure cookie whenever ITS connection is HTTPS, whatever the
                // proxy-to-app hop was — and http://localhost counts as secure, so development works.
                // Do not widen ForwardedHeaders to "fix" this; nothing is broken.
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

                options.ExpireTimeSpan = web.SessionLifetime;
                options.SlidingExpiration = true;

                options.LoginPath = PortalSession.LoginPath;
                options.LogoutPath = PortalSession.LogoutPath;
                options.AccessDeniedPath = PortalSession.DeniedPath;

                options.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = PortalSession.ValidatePrincipalAsync,
                    OnRedirectToLogin = PortalSession.RedirectRelativeAsync,
                    OnRedirectToAccessDenied = PortalSession.RedirectRelativeAsync,
                    OnRedirectToLogout = PortalSession.RedirectRelativeAsync,
                    OnRedirectToReturnUrl = PortalSession.RedirectRelativeAsync,
                };
            });

        services.AddAntiforgery(options =>
        {
            // NOT `SecurePolicy = Always`, although that is what the session cookie uses. Antiforgery
            // refuses to run at all when that is set and the request is not HTTPS — and behind a
            // TLS-terminating proxy every request this process sees is plain http, so the portal would
            // throw on its first form. See AlwaysSecureCookieBuilder for how the cookie is still Secure.
            options.Cookie = new AlwaysSecureCookieBuilder
            {
                Name = PortalSession.CookieName + "-Antiforgery",
                Path = "/",
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
            };
        });

        // The key ring encrypts the session cookie and the antiforgery tokens. Persisted in Postgres —
        // see WorkLedgerDbContext.DataProtectionKeys for why, and for why unencrypted — but only while
        // the portal is on: this is PersistKeysToDbContext's own registration, resolved lazily so it can
        // read Web:Enabled, and with the portal off the default ring stays in place and nothing is
        // written to that table.
        services.AddDataProtection();
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
            new ConfigureOptions<KeyManagementOptions>(options =>
            {
                if (sp.GetRequiredService<WebOptions>().Enabled)
                {
                    options.XmlRepository = new EntityFrameworkCoreXmlRepository<WorkLedgerDbContext>(
                        sp, sp.GetRequiredService<ILoggerFactory>());
                }
            }));
        services.AddOptions<DataProtectionOptions>()
            .Configure<WebOptions>((options, web) =>
            {
                if (web.Enabled)
                {
                    options.ApplicationDiscriminator = DataProtectionApplicationName;
                }
            });
    }

    /// <summary>
    /// Hardening headers on every response under <c>/portal</c>, and nowhere else — the JSON API's
    /// responses are unchanged. <c>Cache-Control: no-store</c> is applied unless the endpoint set its own
    /// caching (the static assets do), because portal pages render account details and, from S11's later
    /// pages, one-time tokens that must not land in a shared or back-button cache.
    /// </summary>
    public static void UsePortalHeaders(this WebApplication app) =>
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(WebOptions.PortalPath),
            branch => branch.Use(async (context, next) =>
            {
                context.Response.OnStarting(() =>
                {
                    var headers = context.Response.Headers;
                    headers.ContentSecurityPolicy = ContentSecurityPolicy;
                    headers["Referrer-Policy"] = "no-referrer";
                    headers.XContentTypeOptions = "nosniff";
                    if (!headers.ContainsKey("Cache-Control"))
                    {
                        headers.CacheControl = "no-store";
                    }

                    return Task.CompletedTask;
                });

                await next();
            }));

    /// <summary>
    /// A cookie that is <c>Secure</c> on every response, whatever scheme the request arrived on — the same
    /// outcome <c>CookieSecurePolicy.Always</c> gives the session cookie, without antiforgery's check that
    /// turns <c>Always</c> into an exception on a plain-http request.
    /// <para>
    /// <b>Why that check is wrong for this deployment, not merely inconvenient.</b> It exists to stop an
    /// app issuing a Secure cookie over a connection the browser would never send it back on. Here the
    /// browser's connection is HTTPS at the proxy — <c>Web:PublicBaseUrl</c> is required to be https, or a
    /// loopback address that browsers treat as a secure context — and only the proxy-to-app hop is http,
    /// which the browser never sees. The alternative, trusting <c>X-Forwarded-Proto</c> so the request
    /// looks HTTPS, is the header S10 deliberately does not accept (see <c>Program.cs</c>).
    /// </para>
    /// <para>
    /// It also keeps the <c>__Host-</c> prefix honest: a browser rejects a <c>__Host-</c> cookie that is
    /// not Secure, so a <c>SameAsRequest</c> policy would have broken the cookie outright behind a proxy.
    /// </para>
    /// </summary>
    private sealed class AlwaysSecureCookieBuilder : CookieBuilder
    {
        public override CookieOptions Build(HttpContext context, DateTimeOffset expiresFrom)
        {
            var options = base.Build(context, expiresFrom);
            options.Secure = true;
            return options;
        }
    }

    /// <summary>
    /// The pages and their static assets. The assets opt out of the fallback policy explicitly — they are
    /// endpoints like any other, and a stylesheet behind a bearer token is a login page nobody can read.
    /// </summary>
    public static void MapPortal(this WebApplication app)
    {
        app.MapStaticAssets().AllowAnonymous();
        app.MapRazorPages();
    }
}
