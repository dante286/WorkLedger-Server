using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Reports;
using WorkLedger.Server.Sync;

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
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentContext, CurrentContext>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<ReportService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

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
    });

builder.Services.AddAuthorization(options =>
{
    // Protected by default — endpoints opt out with .AllowAnonymous() (see /, /health, and everything in
    // Auth/AuthEndpoints.cs except /auth/whoami).
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
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
}

// Migrate only. Nothing is seeded: from S6 a fresh database starts with zero users and the first
// POST /auth/register creates one, so the old Account:Name config read is gone with the seed it fed.
if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<WorkLedgerDbContext>().Database.Migrate();
}

// Method, path and status only. Request.Path excludes the query string and no header is logged, which
// is what keeps bearer tokens and the X-Bootstrap-Secret out of the log — every secret this API accepts
// arrives in a header, so widening this line to headers or full URLs would start recording them.
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RequestLog");
    await next();
    logger.LogInformation("{Method} {Path} -> {StatusCode}", context.Request.Method, context.Request.Path, context.Response.StatusCode);
});

// No CORS policy, deliberately. The only client is the Avalonia desktop app, which calls over
// HttpClient and is not a browser origin, so CORS would never be consulted; a permissive policy added
// "just in case" would only hand every page on the internet the ability to drive this API with a token
// it managed to read. A future browser client is an explicit decision, not something left already open.
app.UseAuthentication();
app.UseAuthorization();

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "0.0.0-dev";

app.MapGet("/", () => Results.Ok(new { name = "WorkLedger.Server", version })).AllowAnonymous();

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow })).AllowAnonymous();

app.MapAuthEndpoints();
app.MapSyncEndpoints();
app.MapReportEndpoints();

app.Run();

public partial class Program;
