using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Tests.Data;

/// <summary>
/// What a brand-new deployment looks like after its first boot: the host migrates an empty database
/// itself and seeds nothing (S6).
/// <para>
/// <b>Why a container of its own.</b> Every other fixture either migrates the schema itself rather than
/// letting the host do it (<see cref="PostgresFixture"/>, <c>UserAccountsFixture</c>), or shares its
/// database with suites that add users to it (<c>AuthWebAppFixture</c>), so none of them can say that the
/// host's own startup left the Users table empty.
/// </para>
/// </summary>
public class FreshDatabaseStartupTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("workledger")
        .WithUsername("workledger")
        .WithPassword("workledger")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// No pending migrations afterwards proves the boot is what built the schema, so the zero is a
    /// count of a table the host created rather than one the test did.
    /// </summary>
    [Fact]
    public async Task FirstBoot_MigratesAnEmptyDatabase_AndSeedsNoUsers()
    {
        var settings = TestConfig.RequiredStartupSettings();
        settings["ConnectionStrings:Postgres"] = _container.GetConnectionString();
        settings["Database:MigrateOnStartup"] = "true";
        using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));

        (await host.CreateClient().GetAsync("/health")).EnsureSuccessStatusCode();

        var options = new DbContextOptionsBuilder<WorkLedgerDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        using var db = new WorkLedgerDbContext(options);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.Devices.CountAsync());
    }
}
