namespace WorkLedger.Server.Tests;

/// <summary>
/// Program.cs fails fast at startup if Jwt:SigningKey/Bootstrap:Secret are missing, so every
/// WebApplicationFactory&lt;Program&gt; in tests — even ones unrelated to auth — must supply these.
/// </summary>
internal static class TestConfig
{
    public const string BootstrapSecret = "test-bootstrap-secret-do-not-use-in-prod";

    public static Dictionary<string, string?> RequiredAuthSettings() => new()
    {
        ["Jwt:SigningKey"] = "test-signing-key-for-unit-tests-only-0123456789",
        ["Jwt:Issuer"] = "WorkLedger.Server.Tests",
        ["Jwt:Audience"] = "WorkLedger.Client.Tests",
        ["Bootstrap:Secret"] = BootstrapSecret,
        ["Account:Name"] = "Test Account",
    };
}
