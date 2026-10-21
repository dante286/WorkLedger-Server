using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Tests.Web;

namespace WorkLedger.Server.Tests;

/// <summary>
/// What <c>Program.cs</c> does between <c>Build()</c> and serving its first request: refusing to start
/// without its secrets, and saying which forwarded-header mode it is in.
/// <para>
/// None of it needs a database. Each host starts on <see cref="TestConfig.UnusedConnectionString"/> with
/// <c>Database:MigrateOnStartup=false</c>, as <c>HealthEndpointTests</c> does, so a refusal here can only
/// be the key under test and never a connection failure standing in for one.
/// </para>
/// </summary>
public class StartupConfigurationTests
{
    /// <summary>
    /// Blank as well as absent, because compose renders an unset <c>${Jwt__SigningKey:-}</c> as
    /// <c>""</c>. The value is overridden with null rather than left out of the dictionary so a
    /// <c>Jwt__SigningKey</c> in the environment running the tests cannot satisfy the check.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMissingJwtSigningKey_FailsStartup(string? configured)
    {
        using var host = Host(("Jwt:SigningKey", configured));

        var exception = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains("Jwt:SigningKey", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMissingBootstrapSecret_FailsStartup(string? configured)
    {
        using var host = Host(("Bootstrap:Secret", configured));

        var exception = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains("Bootstrap:Secret", exception.Message);
    }

    /// <summary>
    /// The optional JWT keys are passed through compose as <c>${Key:-}</c>, so blank has to mean the
    /// default, not an empty issuer that no token would ever validate against.
    /// </summary>
    [Fact]
    public void BlankOptionalJwtKeys_FallBackToTheirDefaults()
    {
        using var host = Host(("Jwt:Issuer", ""), ("Jwt:Audience", " "), ("Jwt:ExpiryDays", ""));

        var options = host.Services.GetRequiredService<JwtOptions>();

        Assert.Equal("WorkLedger.Server", options.Issuer);
        Assert.Equal("WorkLedger.Client", options.Audience);
        Assert.Equal(365, options.ExpiryDays);
    }

    /// <summary>
    /// S10 item 15: the forwarded-header mode is logged in <b>both</b> positions, so "I never turned it
    /// on" and "I turned it on and it is not working" can be told apart from <c>docker compose logs</c>.
    /// </summary>
    [Theory]
    [InlineData(null, "Not trusting X-Forwarded-For (Network:TrustedProxies is unset)")]
    [InlineData("10.0.0.1, 10.1.0.0/16", "Trusting X-Forwarded-For from 10.0.0.1, 10.1.0.0/16")]
    public async Task TheForwardedHeaderMode_IsLoggedAtStartup_EitherWay(string? trustedProxies, string expected)
    {
        var logs = new CapturedLogs();
        using var baseHost = Host(("Network:TrustedProxies", trustedProxies));
        using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging
            .AddProvider(logs)
            .AddFilter<CapturedLogs>(category: null, LogLevel.Trace)));

        await host.CreateClient().GetAsync("/health");

        var startup = Assert.Single(logs.Lines, line => line.StartsWith("Startup: ", StringComparison.Ordinal));
        Assert.Contains(expected, startup);
    }

    private static WebApplicationFactory<Program> Host(params (string Key, string? Value)[] overrides)
    {
        var settings = TestConfig.RequiredStartupSettings();
        settings["Database:MigrateOnStartup"] = "false";
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));
    }
}
