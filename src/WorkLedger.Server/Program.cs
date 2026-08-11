using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;
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

builder.Services.AddDbContext<WorkLedgerDbContext>((sp, options) =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("Postgres")
        ?? BuildConnectionStringFromPostgresEnv(configuration)
        ?? throw new InvalidOperationException(
            "No Postgres connection string configured. Set ConnectionStrings:Postgres (or ConnectionStrings__Postgres " +
            "as an env var), or set POSTGRES_HOST/POSTGRES_DB/POSTGRES_USER/POSTGRES_PASSWORD.");
    options.UseNpgsql(connectionString);
});

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

builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentContext, CurrentContext>();
builder.Services.AddScoped<SyncService>();

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
    // Protected by default — endpoints opt out with .AllowAnonymous() (see /, /health, /auth/register-device).
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();

// Force eager resolution so a missing signing key/bootstrap secret fails startup immediately
// rather than lazily on the first request that happens to need them.
using (var validationScope = app.Services.CreateScope())
{
    validationScope.ServiceProvider.GetRequiredService<JwtOptions>();
    validationScope.ServiceProvider.GetRequiredService<BootstrapOptions>();
}

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    var accountName = app.Configuration["Account:Name"] ?? "Default";
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<WorkLedgerDbContext>();
    db.Database.Migrate();
    await SeedV1AccountAsync(db, accountName);
}

app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RequestLog");
    await next();
    logger.LogInformation("{Method} {Path} -> {StatusCode}", context.Request.Method, context.Request.Path, context.Response.StatusCode);
});

app.UseAuthentication();
app.UseAuthorization();

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "0.0.0-dev";

app.MapGet("/", () => Results.Ok(new { name = "WorkLedger.Server", version })).AllowAnonymous();

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow })).AllowAnonymous();

app.MapPost("/auth/register-device", async (
    RegisterDeviceRequest request,
    HttpRequest httpRequest,
    WorkLedgerDbContext db,
    JwtTokenService tokenService,
    BootstrapOptions bootstrap) =>
{
    if (!httpRequest.Headers.TryGetValue("X-Bootstrap-Secret", out var provided) ||
        !FixedTimeEquals(provided.ToString(), bootstrap.Secret))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.DeviceId))
    {
        return Results.BadRequest(new { error = "DeviceId is required." });
    }

    // v1 is single-account — the seeded row is the only one, so the first (and only) match is authoritative.
    var account = await db.Accounts.OrderBy(a => a.Id).FirstAsync();

    var device = await db.Devices.SingleOrDefaultAsync(d => d.AccountId == account.Id && d.DeviceId == request.DeviceId);
    var now = DateTime.UtcNow;
    if (device is null)
    {
        device = new Device { AccountId = account.Id, DeviceId = request.DeviceId, Label = request.Label, LastSeenUtc = now };
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

    await db.SaveChangesAsync();

    var (token, expiresAtUtc) = tokenService.IssueToken(account.Id, device.Id, device.DeviceId);
    return Results.Ok(new RegisterDeviceResponse(token, expiresAtUtc));
}).AllowAnonymous();

app.MapGet("/auth/whoami", (ICurrentContext currentContext) =>
    Results.Ok(new WhoAmIResponse(currentContext.AccountId, currentContext.DeviceId)));

app.MapSyncEndpoints();

app.Run();

static bool FixedTimeEquals(string provided, string expected) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));

static async Task SeedV1AccountAsync(WorkLedgerDbContext db, string accountName)
{
    if (!await db.Accounts.AnyAsync())
    {
        db.Accounts.Add(new Account { Name = accountName });
        await db.SaveChangesAsync();
    }
}

static string? BuildConnectionStringFromPostgresEnv(IConfiguration configuration)
{
    var host = configuration["POSTGRES_HOST"];
    var db = configuration["POSTGRES_DB"];
    var user = configuration["POSTGRES_USER"];
    var password = configuration["POSTGRES_PASSWORD"];

    if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(db) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
    {
        return null;
    }

    var port = configuration["POSTGRES_PORT"] ?? "5432";
    return $"Host={host};Port={port};Database={db};Username={user};Password={password}";
}

public partial class Program;
