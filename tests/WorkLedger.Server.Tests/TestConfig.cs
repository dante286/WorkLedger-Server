namespace WorkLedger.Server.Tests;

/// <summary>
/// Program.cs fails fast at startup if Jwt:SigningKey/Bootstrap:Secret/ConnectionStrings:Postgres are
/// missing, so every WebApplicationFactory&lt;Program&gt; in tests — even ones unrelated to auth — must
/// supply these.
/// <para>
/// The JWT values are constants rather than literals in the dictionary because tests mint their own
/// tokens and must sign them with the same key the host validates against — see
/// AuthWebAppFixture.EnrollDeviceAsync. There is no <c>Account:Name</c> here any more: S6 removed the
/// startup seed that read it, so a test host starts with zero users.
/// </para>
/// </summary>
internal static class TestConfig
{
    public const string BootstrapSecret = "test-bootstrap-secret-do-not-use-in-prod";
    public const string JwtSigningKey = "test-signing-key-for-unit-tests-only-0123456789";
    public const string JwtIssuer = "WorkLedger.Server.Tests";
    public const string JwtAudience = "WorkLedger.Client.Tests";

    /// <summary>
    /// A syntactically valid connection string that points at nothing, overridden by every fixture that
    /// actually has a database (AuthWebAppFixture swaps in its Testcontainers one). It exists so a test
    /// that needs no database — HealthEndpointTests, which also turns off Database:MigrateOnStartup —
    /// still clears the startup connection-string check without depending on which appsettings file
    /// WebApplicationFactory happens to load.
    /// </summary>
    public const string UnusedConnectionString =
        "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none";

    public static Dictionary<string, string?> RequiredStartupSettings() => new()
    {
        ["Jwt:SigningKey"] = JwtSigningKey,
        ["Jwt:Issuer"] = JwtIssuer,
        ["Jwt:Audience"] = JwtAudience,
        ["Bootstrap:Secret"] = BootstrapSecret,
        ["ConnectionStrings:Postgres"] = UnusedConnectionString,
    };
}
