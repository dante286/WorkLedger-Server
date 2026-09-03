using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Tests.Data;

/// <summary>
/// One throwaway Postgres container per test class (via the collection below), migrated once in
/// InitializeAsync. Each test gets its own WorkLedgerDbContext instance via CreateContext() so
/// tests don't share a change tracker, but they do share the underlying database — tests must
/// use their own User row to avoid colliding with each other.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("workledger")
        .WithUsername("workledger")
        .WithPassword("workledger")
        .Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    public WorkLedgerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WorkLedgerDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        return new WorkLedgerDbContext(options);
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
