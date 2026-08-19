namespace WorkLedger.Server.Data;

/// <summary>
/// The resolved Postgres connection string — one place where it is assembled, one place where a
/// missing one is caught.
/// <para>
/// Two supported inputs, in precedence order: <c>ConnectionStrings:Postgres</c> (a full DSN, which is
/// what an external or managed Postgres needs), then the four <c>POSTGRES_HOST</c> /
/// <c>POSTGRES_DB</c> / <c>POSTGRES_USER</c> / <c>POSTGRES_PASSWORD</c> variables (plus optional
/// <c>POSTGRES_PORT</c>). docker-compose.yml passes the latter set so the database password lives in
/// exactly one place in <c>.env</c> instead of being duplicated into a DSN.
/// </para>
/// <para>
/// A <b>blank</b> value counts as unset rather than as a configured empty string. Compose renders an
/// unset optional override as <c>""</c> (<c>ConnectionStrings__Postgres: ${ConnectionStrings__Postgres:-}</c>),
/// and a plain null-coalesce would have accepted that empty string and then failed on connect with a
/// driver error instead of falling through to the POSTGRES_* variables.
/// </para>
/// <para>
/// Program.cs resolves this eagerly at startup rather than on first use: with
/// <c>Database:MigrateOnStartup</c> turned off, the first thing to notice a missing connection string
/// would otherwise be a 500 on a device's first sync. The failure message names the configuration keys
/// and never their values — a misconfigured deployment must not put a password in its logs.
/// </para>
/// </summary>
public sealed record PostgresConnection(string ConnectionString)
{
    public static PostgresConnection Resolve(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("Postgres");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new PostgresConnection(configured);
        }

        var host = Configured(configuration, "POSTGRES_HOST");
        var database = Configured(configuration, "POSTGRES_DB");
        var user = Configured(configuration, "POSTGRES_USER");
        var password = Configured(configuration, "POSTGRES_PASSWORD");

        if (host is null || database is null || user is null || password is null)
        {
            throw new InvalidOperationException(
                "No Postgres connection string configured. Set ConnectionStrings:Postgres " +
                "(ConnectionStrings__Postgres as an environment variable), or all four of " +
                "POSTGRES_HOST, POSTGRES_DB, POSTGRES_USER and POSTGRES_PASSWORD — the app refuses to " +
                "start without a database it can reach.");
        }

        var port = Configured(configuration, "POSTGRES_PORT") ?? "5432";
        return new PostgresConnection(
            $"Host={host};Port={port};Database={database};Username={user};Password={password}");
    }

    private static string? Configured(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
