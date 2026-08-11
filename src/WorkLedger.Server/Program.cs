using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

var app = builder.Build();

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

public partial class Program;
