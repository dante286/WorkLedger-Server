namespace WorkLedger.Server.Tests;

/// <summary>
/// Program.cs fails fast at startup if Jwt:SigningKey/Bootstrap:Secret are missing, so every
/// WebApplicationFactory&lt;Program&gt; in tests — even ones unrelated to auth — must supply these.
/// <para>
/// The JWT values are constants rather than literals in the dictionary because a test that needs a
/// token for an account <c>/auth/register-device</c> will not issue one (that endpoint always binds to
/// the single seeded v1 account) has to sign it with the same key the host validates against — see
/// ReportEndpointTests.
/// </para>
/// </summary>
internal static class TestConfig
{
    public const string BootstrapSecret = "test-bootstrap-secret-do-not-use-in-prod";
    public const string JwtSigningKey = "test-signing-key-for-unit-tests-only-0123456789";
    public const string JwtIssuer = "WorkLedger.Server.Tests";
    public const string JwtAudience = "WorkLedger.Client.Tests";

    public static Dictionary<string, string?> RequiredAuthSettings() => new()
    {
        ["Jwt:SigningKey"] = JwtSigningKey,
        ["Jwt:Issuer"] = JwtIssuer,
        ["Jwt:Audience"] = JwtAudience,
        ["Bootstrap:Secret"] = BootstrapSecret,
        ["Account:Name"] = "Test Account",
    };
}
