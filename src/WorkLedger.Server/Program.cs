using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WorkLedger.Server;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Reports;
using WorkLedger.Server.Sync;
using WorkLedger.Server.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

// NOTE: WebApplicationFactory (used by tests) splices its config overrides into the host only at
// the moment builder.Build() is called — code that reads builder.Configuration eagerly, here,
// before Build(), never sees those overrides. So every config-dependent piece below is wired as a
// lazy DI factory/configure delegate that resolves IConfiguration at first-use time (after
// Build()), and the fail-fast checks are forced to run right after Build() (see below) so
// misconfiguration is still caught at startup, not on the first request.

builder.Services.AddSingleton(sp => PostgresConnection.Resolve(sp.GetRequiredService<IConfiguration>()));

builder.Services.AddDbContext<WorkLedgerDbContext>((sp, options) =>
    options.UseNpgsql(sp.GetRequiredService<PostgresConnection>().ConnectionString));

builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var signingKey = configuration["Jwt:SigningKey"];
    if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
    {
        throw new InvalidOperationException(
            "Jwt:SigningKey is missing or too short. Set Jwt:SigningKey (or Jwt__SigningKey env var) to a random " +
            "secret of at least 32 bytes — the app refuses to start without it.");
    }

    var issuer = configuration["Jwt:Issuer"] ?? "WorkLedger.Server";
    var audience = configuration["Jwt:Audience"] ?? "WorkLedger.Client";
    var expiryDays = configuration.GetValue("Jwt:ExpiryDays", 365);
    return new JwtOptions(signingKey, issuer, audience, expiryDays);
});

builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var secret = configuration["Bootstrap:Secret"];
    if (string.IsNullOrWhiteSpace(secret))
    {
        throw new InvalidOperationException(
            "Bootstrap:Secret is missing. Set Bootstrap:Secret (or Bootstrap__Secret env var) so device " +
            "registration can be gated — the app refuses to start without it.");
    }

    return new BootstrapOptions(secret);
});

builder.Services.AddSingleton(sp => AuthOptions.Resolve(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<JwtTokenService>();
// Backs TokenRevocation's per-device (UserId, SecurityStamp) snapshots, which are what keep the
// revocation check off the database on every authenticated request.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentContext, CurrentContext>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<ReportService>();

// The account rules (S11). Every route in Auth/ is a thin caller of these, so anything else that signs
// people in or manages accounts calls the same code rather than a copy of it.
builder.Services.AddScoped<CredentialCheck>();
builder.Services.AddScoped<AccountDeletion>();
builder.Services.AddScoped<AccountSelfService>();
builder.Services.AddScoped<AdminOperations>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<PasswordResetService>();
builder.Services.AddScoped<RegistrationService>();

// The web portal (S11): Razor Pages under /portal behind Web:Enabled, default off. Registered either way
// and inert until mapped below — see PortalSetup for why the gate is on the pipeline, not here.
builder.Services.AddPortal();

// JwtBearer stays the DEFAULT scheme, so the fallback and admin policies — and with them every JSON
// route — authenticate by bearer token only. The portal's cookie scheme sits beside it and is consulted
// only by the portal's own policies below; see PortalSession for why the two are never merged.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    .AddCookie(PortalSession.Scheme);

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtOptions>((options, jwtOptions) =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        // Revocation. Runs only after the checks above have all passed, so a forged, expired or
        // wrong-audience token never reaches the database. See TokenRevocation for what it costs and
        // what Auth:StampCacheSeconds trades away.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = TokenRevocation.OnTokenValidatedAsync,
        };
    });

builder.Services.AddSingleton(sp => TrustedProxies.Resolve(sp.GetRequiredService<IConfiguration>()));

// Only consulted when Network:TrustedProxies names something; see below, and TrustedProxies' own doc
// for why the operator has to say rather than the process guessing.
builder.Services.AddOptions<ForwardedHeadersOptions>()
    .Configure<TrustedProxies>((options, trustedProxies) =>
    {
        // XForwardedFor ONLY. XForwardedProto and XForwardedHost change how URLs are generated and how
        // HTTPS is detected; nothing here needs either, and accepting headers you do not consume is how
        // a proxy misconfiguration turns into a redirect loop nobody can find.
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;

        // One hop. The header is a chain, and each additional hop taken on trust is another address the
        // caller controls — with a single proxy in front, the last entry is the only one it wrote.
        options.ForwardLimit = 1;

        // NEVER KnownNetworks.Clear() / KnownProxies.Clear() to "make it work". That is precisely the
        // change that trusts X-Forwarded-For from anybody and hands every caller an unlimited rate-limit
        // bucket per address they invent. The defaults (loopback) stay, and the operator's list is added
        // to them.
        foreach (var address in trustedProxies.Addresses)
        {
            options.KnownProxies.Add(address);
        }

        foreach (var network in trustedProxies.Networks)
        {
            // KnownIPNetworks, not the KnownNetworks property that took ASP.NET's own IPNetwork type —
            // that one is obsolete as of .NET 9 and warns (ASPDEPR005).
            options.KnownIPNetworks.Add(network);
        }
    });

// Per-IP throttling for the endpoints that take credentials from an anonymous caller. The per-user
// lockout in /auth/login defends one account; this defends the box against a caller spraying many
// emails, and against registration being used to fill the table.
//
// It partitions on Connection.RemoteIpAddress. Behind a reverse proxy that is the proxy for every
// request — so the whole deployment would share one bucket — unless Network:TrustedProxies names the
// hop, in which case the ForwardedHeaders middleware below has already replaced it with the caller's
// real address. Unset means no forwarded-header processing at all, which is the safe default and the
// behaviour this had before the setting existed.
builder.Services.AddRateLimiter(options =>
{
    // Default is 503, which reads as "the server is broken" rather than "you are going too fast".
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(AuthEndpoints.RateLimitPolicy, httpContext =>
    {
        // A GET takes no credential, so it never spends a permit. Every JSON route under this policy is a
        // POST, so this changes nothing there; it exists for the portal, whose policy attaches to a whole
        // Razor page, and whose GET only renders the form the POST then submits.
        if (HttpMethods.IsGet(httpContext.Request.Method) || HttpMethods.IsHead(httpContext.Request.Method))
        {
            return RateLimitPartition.GetNoLimiter("reads");
        }

        var auth = httpContext.RequestServices.GetRequiredService<AuthOptions>();
        if (auth.LoginRateLimitPerMinute <= 0)
        {
            // Zero or less means off, the same way Auth:StampCacheSeconds does. A limiter permitting
            // zero requests would make login unreachable, which is not what an operator typing 0 means.
            return RateLimitPartition.GetNoLimiter("disabled");
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = auth.LoginRateLimitPerMinute,
                Window = auth.LoginRateLimitWindow,
                // No queue: a caller past the limit gets an immediate 429 rather than a held-open
                // connection, and nothing here is worth waiting in line for.
                QueueLimit = 0,
            });
    });

    options.OnRejected = (context, _) =>
    {
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? (int)Math.Ceiling(retryAfter.TotalSeconds)
            : (int)Math.Ceiling(context.HttpContext.RequestServices
                .GetRequiredService<AuthOptions>().LoginRateLimitWindow.TotalSeconds);
        seconds = Math.Max(1, seconds);
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        // A browser shows a bare 429 as a blank page, so the portal gets a sentence. The JSON API's 429
        // stays bodiless, as it always has been: a client reads Retry-After.
        if (context.HttpContext.Request.Path.StartsWithSegments(WebOptions.PortalPath))
        {
            context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
            return new ValueTask(context.HttpContext.Response.WriteAsync(
                $"Too many sign-in attempts from this address. Try again in {seconds} seconds.",
                context.HttpContext.RequestAborted));
        }

        return ValueTask.CompletedTask;
    };
});

builder.Services.AddAuthorization(options =>
{
    // Protected by default — endpoints opt out with .AllowAnonymous() (see /, /health, and everything in
    // Auth/AuthEndpoints.cs except /auth/whoami and /auth/password).
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // The operator role (S10). The claim is not in the token — TokenRevocation puts it on the principal
    // from the IsAdmin column on every request, so a demotion takes effect without waiting for a
    // year-long token to expire. See TokenRevocation and AdminEndpoints.
    options.AddPolicy(TokenRevocation.AdminPolicy, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(TokenRevocation.AdminClaim, "true"));

    // The portal's two policies (S11). Each names the cookie scheme explicitly, which is what makes it
    // authenticate by cookie ONLY: a bearer token presented to a portal page is not consulted, and the
    // page redirects to sign-in. The admin claim is the same one, put there from the same column — see
    // PortalSession.ValidatePrincipalAsync.
    options.AddPolicy(PortalSession.UserPolicy, policy => policy
        .AddAuthenticationSchemes(PortalSession.Scheme)
        .RequireAuthenticatedUser());
    options.AddPolicy(PortalSession.AdminPolicy, policy => policy
        .AddAuthenticationSchemes(PortalSession.Scheme)
        .RequireAuthenticatedUser()
        .RequireClaim(TokenRevocation.AdminClaim, "true"));
});

var app = builder.Build();

// Force eager resolution so missing critical config — signing key, bootstrap secret, connection
// string — fails startup immediately rather than lazily on the first request that happens to need it.
// A container that exits naming a configuration key is diagnosable from `docker compose logs`; one that
// starts and then 500s on a device's first sync is not.
using (var validationScope = app.Services.CreateScope())
{
    validationScope.ServiceProvider.GetRequiredService<JwtOptions>();
    validationScope.ServiceProvider.GetRequiredService<BootstrapOptions>();
    validationScope.ServiceProvider.GetRequiredService<PostgresConnection>();
    validationScope.ServiceProvider.GetRequiredService<AuthOptions>();
    validationScope.ServiceProvider.GetRequiredService<TrustedProxies>();
    validationScope.ServiceProvider.GetRequiredService<WebOptions>();
}

// Migrate only. Nothing is seeded: from S6 a fresh database starts with zero users and the first
// POST /auth/register creates one, so the old Account:Name config read is gone with the seed it fed.
if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<WorkLedgerDbContext>().Database.Migrate();
}

// First in the pipeline, before anything reads Connection.RemoteIpAddress — the request log below, and
// the rate limiter further down. Registered only when the operator has named a proxy: with the setting
// unset this middleware is absent entirely rather than present-and-trusting-nothing, so there is no
// behaviour to accidentally change and nothing to pay for.
var trustedProxies = app.Services.GetRequiredService<TrustedProxies>();
if (trustedProxies.IsConfigured)
{
    app.UseForwardedHeaders();
}

// Said out loud at startup, both ways. An operator debugging "everyone is getting 429" needs to see
// which of the two modes they are in without reading the image's configuration back out of it.
app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup").LogInformation(
    trustedProxies.IsConfigured
        ? "Trusting X-Forwarded-For from {TrustedProxies}; rate limits partition on the forwarded client address."
        : "Not trusting X-Forwarded-For (Network:TrustedProxies is unset); rate limits partition on the socket address, which behind a reverse proxy is one bucket for the whole deployment.",
    trustedProxies.Describe());

// Method, path and status only. Request.Path excludes the query string and no header is logged, which
// is what keeps bearer tokens and the X-Bootstrap-Secret out of the log — every secret the JSON API
// accepts arrives in a header, so widening this line to headers or full URLs would start recording them.
// The portal (S11) adds the first secrets that arrive some other way — invite and reset tokens — and
// they travel only in a URL fragment (never sent to the server) or a POST body. NO TOKEN MAY EVER BE A
// PATH SEGMENT: this line would log it.
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RequestLog");
    await next();
    logger.LogInformation("{Method} {Path} -> {StatusCode}", context.Request.Method, context.Request.Path, context.Response.StatusCode);
});

// Portal responses only, and only when the portal is on. Registered before authentication so the
// sign-in redirects a challenge produces carry the headers too.
var web = app.Services.GetRequiredService<WebOptions>();
if (web.Enabled)
{
    app.UsePortalHeaders();
}

// No CORS policy, deliberately. The only client is the Avalonia desktop app, which calls over
// HttpClient and is not a browser origin, so CORS would never be consulted; a permissive policy added
// "just in case" would only hand every page on the internet the ability to drive this API with a token
// it managed to read. A future browser client is an explicit decision, not something left already open.
app.UseAuthentication();
app.UseAuthorization();

// After authentication so a rejected request is still logged with the identity it presented, and
// before the endpoints whose .RequireRateLimiting() calls it enforces.
app.UseRateLimiter();

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "0.0.0-dev";

// With the portal on, a browser landing on the bare origin is sent to it; anything else — the client, a
// health probe, curl — gets the same JSON as ever. With the portal off this is exactly the S10 route.
// The redirect is host-relative, so it never repeats the caller's Host header back to them.
app.MapGet("/", (HttpRequest request) =>
        web.Enabled && AcceptsHtml(request)
            ? Results.Redirect(WebOptions.PortalPath)
            : Results.Ok(new { name = "WorkLedger.Server", version }))
    .AllowAnonymous();

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow })).AllowAnonymous();

app.MapAuthEndpoints();
app.MapSyncEndpoints();
app.MapReportEndpoints();

if (web.Enabled)
{
    app.MapPortal();
}

app.Run();

// A browser navigating asks for text/html explicitly; `*/*` alone is what non-browser clients send, and
// it stays JSON — the same reading /reports gives Accept.
static bool AcceptsHtml(HttpRequest request) =>
    request.GetTypedHeaders().Accept.Any(accept =>
        accept.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase));

public partial class Program;
