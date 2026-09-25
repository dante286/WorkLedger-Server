using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Tests.Auth;

/// <summary>
/// A Postgres container of its own, plus a host built per test.
/// <para>
/// <b>Why this cannot share <see cref="AuthWebAppFixture"/>.</b> Several behaviours here are answers
/// about the <i>whole database</i> rather than about one row: <c>/auth/claim</c> needs exactly one
/// credential-less user, <c>/auth/capabilities</c> reports <c>HasUnclaimedAccount</c> off the same
/// count, and login's account-lockout and rate-limit assertions need a user nothing else is signing in
/// as. A suite asserting on those cannot run against a database other suites are adding users to, and
/// it must not share a rate limiter with them either, so it gets a private database and
/// <see cref="ResetAsync"/> clears it between tests. Test cases in one xUnit class never run
/// concurrently, so the reset is a clean slate rather than a race.
/// </para>
/// <para>
/// Hosts are built per test because the assertions <i>are</i> about configuration —
/// <c>Auth:RegistrationEnabled</c> in both positions, overridden lockout numbers, a tiny lockout window,
/// a stamp cache switched off, a rate limit of three — and a WebApplicationFactory bakes its
/// configuration in at startup. A fresh host also means a fresh rate limiter and a fresh stamp cache,
/// so one test cannot exhaust or poison the next one's. The schema is migrated once here, so
/// those hosts start with <c>Database:MigrateOnStartup=false</c> and cost almost nothing.
/// </para>
/// </summary>
public sealed class UserAccountsFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("workledger")
        .WithUsername("workledger")
        .WithPassword("workledger")
        .Build();

    public string BootstrapSecret => TestConfig.BootstrapSecret;

    /// <summary>
    /// This fixture's database, for a suite that needs to build its own
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> rather than take one from
    /// <see cref="CreateHost"/> — <c>ProxyRateLimitTests</c> has to register an <c>IStartupFilter</c>
    /// too, which <see cref="CreateHost"/> deliberately has no parameter for.
    /// </summary>
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public WorkLedgerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WorkLedgerDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        return new WorkLedgerDbContext(options);
    }

    /// <summary>
    /// A host on this fixture's database, with <paramref name="overrides"/> layered over the settings
    /// every host needs to start at all. Dispose it when the test is done with it.
    /// </summary>
    public WebApplicationFactory<Program> CreateHost(params (string Key, string? Value)[] overrides)
    {
        var settings = TestConfig.RequiredStartupSettings();
        settings["ConnectionStrings:Postgres"] = _container.GetConnectionString();
        settings["Database:MigrateOnStartup"] = "false";
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));
    }

    /// <summary>
    /// Empties every table, so each test starts from a database with zero users — and with no Data
    /// Protection keys, so a portal test that asserts on the shared key ring cannot pass on a key some
    /// earlier test left behind.
    /// </summary>
    public async Task ResetAsync()
    {
        using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            @"TRUNCATE ""TimeEntries"", ""TaskItems"", ""Projects"", ""Devices"", ""Invites"", ""PasswordResets"", ""Users"", ""DataProtectionKeys"" RESTART IDENTITY CASCADE;");
    }
}

[CollectionDefinition(nameof(UserAccountsCollection))]
public sealed class UserAccountsCollection : ICollectionFixture<UserAccountsFixture>;
