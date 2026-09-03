using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Tests.Auth;

/// <summary>
/// One throwaway Postgres container + one hosted WebApplicationFactory per test class in
/// <see cref="AuthCollection"/>.
/// <para>
/// <b>Nothing is seeded.</b> Before S6 the host provisioned a v1 account at startup and every test in
/// this collection borrowed it; from S6 a fresh database starts with zero users, so each test asks for
/// its own through <see cref="NewUserAsync"/> / <see cref="EnrollDeviceAsync"/>.
/// </para>
/// <para>
/// <b>Why these mint tokens instead of calling <c>POST /auth/register-device</c>.</b> That endpoint now
/// only works while <i>exactly one</i> user exists — it returns 410 beyond that, deliberately, rather
/// than guessing which account a device belongs to. Every class in this collection shares one database,
/// so the first test to create a second user would otherwise break enrollment for all the others.
/// Minting directly also gives each test its own user, which is what keeps one test's time entries out
/// of another's report total. The endpoint's own behaviour is covered against a private database in
/// <see cref="UserAccountsFixture"/>'s suites.
/// </para>
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

    /// <summary>Signs tokens with the same key, issuer and audience the hosted app validates against.</summary>
    public JwtTokenService Tokens { get; } = new(new JwtOptions(
        TestConfig.JwtSigningKey, TestConfig.JwtIssuer, TestConfig.JwtAudience, ExpiryDays: 1));

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var settings = TestConfig.RequiredStartupSettings();
        settings["ConnectionStrings:Postgres"] = _container.GetConnectionString();
        settings["Database:MigrateOnStartup"] = "true";

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));

        // Force host startup (and the migration) now rather than lazily on the first test.
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

    /// <summary>
    /// A user row with no credentials, which is all a device-scoped test needs — tenancy comes from the
    /// token's <c>user_id</c>, and nothing in sync or reports consults an email or a password hash.
    /// </summary>
    public async Task<int> NewUserAsync(string label)
    {
        using var db = CreateDbContext();
        var user = new User { DisplayName = $"{label} {Guid.NewGuid()}", CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>
    /// Enrolls a device under <paramref name="userId"/> and hands back a client already carrying its
    /// bearer token. Pass the same <paramref name="userId"/> twice to get two devices that see each
    /// other's pushes, which is the shape every cross-device sync assertion needs.
    /// </summary>
    public async Task<EnrolledDevice> EnrollDeviceAsync(int userId, string label)
    {
        using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var device = new Device
        {
            UserId = userId,
            DeviceId = Guid.NewGuid().ToString(),
            Label = label,
            CreatedAt = now,
            LastSeenUtc = now,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        var (token, _) = Tokens.IssueToken(userId, device.Id, device.DeviceId);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new EnrolledDevice(userId, device.DeviceId, client);
    }
}

public sealed record EnrolledDevice(int UserId, string DeviceId, HttpClient Client);

[CollectionDefinition(nameof(AuthCollection))]
public sealed class AuthCollection : ICollectionFixture<AuthWebAppFixture>;
