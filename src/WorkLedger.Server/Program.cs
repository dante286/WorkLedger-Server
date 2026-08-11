using System.Reflection;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Server.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? BuildConnectionStringFromPostgresEnv(builder.Configuration)
    ?? throw new InvalidOperationException(
        "No Postgres connection string configured. Set ConnectionStrings:Postgres (or ConnectionStrings__Postgres " +
        "as an env var), or set POSTGRES_HOST/POSTGRES_DB/POSTGRES_USER/POSTGRES_PASSWORD.");

builder.Services.AddDbContext<WorkLedgerDbContext>(options => options.UseNpgsql(connectionString));

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<WorkLedgerDbContext>().Database.Migrate();
}

app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RequestLog");
    await next();
    logger.LogInformation("{Method} {Path} -> {StatusCode}", context.Request.Method, context.Request.Path, context.Response.StatusCode);
});

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "0.0.0-dev";

app.MapGet("/", () => Results.Ok(new { name = "WorkLedger.Server", version }));

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

app.Run();

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
