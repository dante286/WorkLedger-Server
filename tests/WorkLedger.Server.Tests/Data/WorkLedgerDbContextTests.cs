using Microsoft.EntityFrameworkCore;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Tests.Data;

[Collection(nameof(PostgresCollection))]
public class WorkLedgerDbContextTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_ApplyCleanly_AgainstEmptyContainer()
    {
        using var context = fixture.CreateContext();

        var pending = await context.Database.GetPendingMigrationsAsync();

        Assert.Empty(pending);
    }

    [Fact]
    public async Task Project_RoundTrips_BySyncId_WithServerStampedUpdatedAt()
    {
        var account = await CreateAccountAsync();
        var syncId = Guid.NewGuid();

        using (var writeContext = fixture.CreateContext())
        {
            writeContext.Projects.Add(new Project
            {
                AccountId = account.Id,
                SyncId = syncId,
                Version = 1,
                OriginDeviceId = "device-a",
                Name = "Test Project",
                // UpdatedAt intentionally left unset — the DbContext must stamp it.
            });
            await writeContext.SaveChangesAsync();
        }

        using var readContext = fixture.CreateContext();
        var loaded = await readContext.Projects.SingleAsync(p => p.AccountId == account.Id && p.SyncId == syncId);

        Assert.Equal("Test Project", loaded.Name);
        Assert.NotEqual(default, loaded.UpdatedAt);
        Assert.Equal(DateTimeKind.Utc, loaded.UpdatedAt.Kind);
    }

    [Fact]
    public async Task DuplicateSyncId_ForSameAccount_IsRejectedByUniqueIndex()
    {
        var account = await CreateAccountAsync();
        var syncId = Guid.NewGuid();

        using (var firstContext = fixture.CreateContext())
        {
            firstContext.Projects.Add(new Project { AccountId = account.Id, SyncId = syncId, Version = 1, Name = "First" });
            await firstContext.SaveChangesAsync();
        }

        using var secondContext = fixture.CreateContext();
        secondContext.Projects.Add(new Project { AccountId = account.Id, SyncId = syncId, Version = 1, Name = "Duplicate" });

        await Assert.ThrowsAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    private async Task<Account> CreateAccountAsync()
    {
        using var context = fixture.CreateContext();
        var account = new Account { Name = $"test-{Guid.NewGuid()}" };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }
}
