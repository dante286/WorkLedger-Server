using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Tests.Auth;

/// <summary>
/// One throwaway Postgres container + one hosted WebApplicationFactory per test class (via the
/// collection below). The v1 Account is seeded once at host startup; tests use fresh random
/// DeviceIds so they don't collide with each other on that shared account/device space.
/// </summary>
public sealed class AuthWebAppFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("workledger")
        .WithUsername("workledger")
        .WithPassword("workledger")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string BootstrapSecret => TestConfig.BootstrapSecret;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var settings = TestConfig.RequiredAuthSettings();
        settings["ConnectionStrings:Postgres"] = _container.GetConnectionString();
        settings["Database:MigrateOnStartup"] = "true";

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));

        // Force host startup (and the v1 Account seed) now rather than lazily on the first test.
        using var warmup = Factory.CreateClient();
        await warmup.GetAsync("/health");
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }

    public WorkLedgerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WorkLedgerDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        return new WorkLedgerDbContext(options);
    }
}

[CollectionDefinition(nameof(AuthCollection))]
public sealed class AuthCollection : ICollectionFixture<AuthWebAppFixture>;
