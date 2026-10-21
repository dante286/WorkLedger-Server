using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WorkLedger.Server.Auth;

namespace WorkLedger.Server.Tests;

/// <summary>
/// What <c>Program.cs</c> does between <c>Build()</c> and serving its first request.
/// <para>
/// None of it needs a database. Each host starts on <see cref="TestConfig.UnusedConnectionString"/> with
/// <c>Database:MigrateOnStartup=false</c>, as <c>HealthEndpointTests</c> does.
/// </para>
/// </summary>
public class StartupConfigurationTests
{
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
