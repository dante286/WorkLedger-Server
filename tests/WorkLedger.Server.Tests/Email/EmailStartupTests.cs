using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkLedger.Server.Email;
using WorkLedger.Server.Tests.Web;

namespace WorkLedger.Server.Tests.Email;

/// <summary>
/// S12 items 1 and 5: <c>Email:*</c> is validated at startup, naming the key, and the mode is logged
/// either way. No database, for the reason <c>StartupConfigurationTests</c> gives.
/// </summary>
public class EmailStartupTests
{
    private static readonly (string, string?)[] Portal =
        [("Web:Enabled", "true"), ("Web:PublicBaseUrl", "https://ledger.example.test")];

    private static readonly (string, string?)[] Minimal =
    [
        .. Portal,
        ("Email:Enabled", "true"),
        ("Email:SmtpHost", "smtp.example.test"),
        ("Email:FromAddress", "no-reply@ledger.example.test"),
    ];

    public static TheoryData<string, string?, string[]> Refusals => new()
    {
        { "Email:SmtpHost", null, ["Email:SmtpHost"] },
        { "Email:SmtpHost", "", ["Email:SmtpHost"] },
        { "Email:FromAddress", null, ["Email:FromAddress"] },
        { "Email:FromAddress", "not-an-address", ["Email:FromAddress"] },
        { "Web:Enabled", "false", ["Email:Enabled", "Web:Enabled"] },
        { "Email:SmtpSecurity", "Auto", ["Email:SmtpSecurity"] },
        { "Email:SmtpSecurity", "1", ["Email:SmtpSecurity"] },
        { "Email:SmtpPort", "0", ["Email:SmtpPort"] },
        { "Email:SmtpPort", "five-eight-seven", ["Email:SmtpPort"] },
        { "Email:QueueCapacity", "0", ["Email:QueueCapacity"] },
        { "Email:ResetCooldownMinutes", "-1", ["Email:ResetCooldownMinutes"] },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void AMisconfiguredRelay_FailsStartup_NamingTheKey(string key, string? value, string[] named)
    {
        using var host = Host([.. Minimal, (key, value)]);

        var exception = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        foreach (var name in named)
        {
            Assert.Contains(name, exception.Message);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AUsernameWithoutAPassword_FailsStartup(string? password)
    {
        using var host = Host([.. Minimal, ("Email:SmtpUsername", "resend"), ("Email:SmtpPassword", password)]);

        var exception = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains("Email:SmtpPassword", exception.Message);
    }

    [Fact]
    public void NoEncryptionWithAUsername_FailsStartup()
    {
        using var host = Host(
        [
            .. Minimal,
            ("Email:SmtpSecurity", "None"),
            ("Email:SmtpUsername", "resend"),
            ("Email:SmtpPassword", "re_secret"),
        ]);

        var exception = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains("Email:SmtpSecurity", exception.Message);
        Assert.Contains("plaintext", exception.Message);
    }

    /// <summary>
    /// Off means nothing else is read: an operator who never turned email on is never refused startup
    /// over a half-filled <c>Email:*</c> block, nor over the portal being off.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    public void EmailOff_ReadsNothingElse(string? enabled)
    {
        using var host = Host(("Email:Enabled", enabled), ("Email:SmtpSecurity", "nonsense"), ("Email:SmtpPort", "x"));

        var options = host.Services.GetRequiredService<EmailOptions>();

        Assert.False(options.Enabled);
        Assert.Throws<InvalidOperationException>(() =>
            host.Services.GetRequiredService<EmailOutbox>().TryEnqueue(
                new OutgoingEmail(EmailKind.Test, null, "someone@example.test", new EmailContent("s", "b"))));
    }

    /// <summary>
    /// Off means no mail worker at all, not one that idles; on means exactly one. Checked on the started
    /// host, where the hosted services are the ones that actually run.
    /// </summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task TheMailWorker_RunsOnlyWhileEmailIsOn(bool on, int workers)
    {
        using var host = on ? Host(Minimal) : Host(("Email:Enabled", "false"));
        await host.CreateClient().GetAsync("/health");

        var running = host.Services.GetServices<IHostedService>().OfType<EmailDeliveryService>().ToList();

        Assert.Equal(workers, running.Count);
        Assert.All(running, worker => Assert.False(worker.ExecuteTask?.IsCompleted));
    }

    /// <summary>The Resend example's values, with every other key blank as compose renders it.</summary>
    [Fact]
    public void BlankOptionalKeys_TakeTheirDefaults()
    {
        using var host = Host(
        [
            .. Minimal,
            ("Email:SmtpPort", ""),
            ("Email:SmtpSecurity", " "),
            ("Email:SmtpUsername", "resend"),
            ("Email:SmtpPassword", "re_secret"),
            ("Email:FromName", ""),
            ("Email:QueueCapacity", ""),
        ]);

        var options = host.Services.GetRequiredService<EmailOptions>();

        Assert.True(options.Enabled);
        Assert.Equal(587, options.SmtpPort);
        Assert.Equal(EmailSecurity.StartTls, options.SmtpSecurity);
        Assert.Equal("WorkLedger", options.FromName);
        Assert.Equal(100, options.QueueCapacity);
        Assert.DoesNotContain("re_secret", options.ToString());
    }

    [Theory]
    [InlineData(new string[0], "Email off: invite and reset links are handed over by an admin.")]
    [InlineData(new[] { "on" }, "Email on: relaying via smtp.example.test:587 (StartTls) as no-reply@ledger.example.test.")]
    [InlineData(new[] { "on", "None" }, "(None) as no-reply@ledger.example.test. Email:SmtpSecurity is None, so mail leaves this process unencrypted.")]
    public async Task TheEmailMode_IsLoggedAtStartup_EitherWay(string[] mode, string expected)
    {
        (string, string?)[] settings = mode switch
        {
            [] => [],
            ["on"] => Minimal,
            _ => [.. Minimal, ("Email:SmtpSecurity", "None")],
        };

        var logs = new CapturedLogs();
        using var baseHost = Host(settings);
        using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging
            .AddProvider(logs)
            .AddFilter<CapturedLogs>(category: null, LogLevel.Trace)));

        await host.CreateClient().GetAsync("/health");

        var line = Assert.Single(logs.Lines, line => line.StartsWith("Email: ", StringComparison.Ordinal));
        Assert.Contains(expected, line);
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
