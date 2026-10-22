using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkLedger.Server.Data;
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
        var user = await CreateUserAsync();
        var syncId = Guid.NewGuid();

        using (var writeContext = fixture.CreateContext())
        {
            writeContext.Projects.Add(new Project
            {
                UserId = user.Id,
                SyncId = syncId,
                Version = 1,
                OriginDeviceId = "device-a",
                Name = "Test Project",
                // UpdatedAt intentionally left unset — the DbContext must stamp it.
            });
            await writeContext.SaveChangesAsync();
        }

        using var readContext = fixture.CreateContext();
        var loaded = await readContext.Projects.SingleAsync(p => p.UserId == user.Id && p.SyncId == syncId);

        Assert.Equal("Test Project", loaded.Name);
        Assert.NotEqual(default, loaded.UpdatedAt);
        Assert.Equal(DateTimeKind.Utc, loaded.UpdatedAt.Kind);
    }

    [Fact]
    public async Task DuplicateSyncId_ForSameUser_IsRejectedByUniqueIndex()
    {
        var user = await CreateUserAsync();
        var syncId = Guid.NewGuid();

        using (var firstContext = fixture.CreateContext())
        {
            firstContext.Projects.Add(new Project { UserId = user.Id, SyncId = syncId, Version = 1, Name = "First" });
            await firstContext.SaveChangesAsync();
        }

        using var secondContext = fixture.CreateContext();
        secondContext.Projects.Add(new Project { UserId = user.Id, SyncId = syncId, Version = 1, Name = "Duplicate" });

        await Assert.ThrowsAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task DuplicateTaskItemSyncId_ForSameUser_IsRejectedByUniqueIndex()
    {
        var user = await CreateUserAsync();
        var syncId = Guid.NewGuid();
        var projectSyncId = Guid.NewGuid();

        using (var firstContext = fixture.CreateContext())
        {
            firstContext.TaskItems.Add(new TaskItem { UserId = user.Id, SyncId = syncId, ProjectSyncId = projectSyncId, Version = 1, Name = "First" });
            await firstContext.SaveChangesAsync();
        }

        using var secondContext = fixture.CreateContext();
        secondContext.TaskItems.Add(new TaskItem { UserId = user.Id, SyncId = syncId, ProjectSyncId = projectSyncId, Version = 1, Name = "Duplicate" });

        await AssertUniqueViolationAsync(secondContext);
    }

    [Fact]
    public async Task DuplicateTimeEntrySyncId_ForSameUser_IsRejectedByUniqueIndex()
    {
        var user = await CreateUserAsync();
        var syncId = Guid.NewGuid();
        var taskSyncId = Guid.NewGuid();
        var start = DateTime.UtcNow;

        using (var firstContext = fixture.CreateContext())
        {
            firstContext.TimeEntries.Add(new TimeEntry { UserId = user.Id, SyncId = syncId, TaskSyncId = taskSyncId, Version = 1, StartTime = start });
            await firstContext.SaveChangesAsync();
        }

        using var secondContext = fixture.CreateContext();
        secondContext.TimeEntries.Add(new TimeEntry { UserId = user.Id, SyncId = syncId, TaskSyncId = taskSyncId, Version = 1, StartTime = start });

        await AssertUniqueViolationAsync(secondContext);
    }

    [Fact]
    public async Task DuplicateDeviceId_ForSameUser_IsRejectedByUniqueIndex()
    {
        var user = await CreateUserAsync();
        var deviceId = Guid.NewGuid().ToString();

        using (var firstContext = fixture.CreateContext())
        {
            firstContext.Devices.Add(new Device { UserId = user.Id, DeviceId = deviceId, Label = "first" });
            await firstContext.SaveChangesAsync();
        }

        using var secondContext = fixture.CreateContext();
        secondContext.Devices.Add(new Device { UserId = user.Id, DeviceId = deviceId, Label = "duplicate" });

        await AssertUniqueViolationAsync(secondContext);
    }

    /// <summary>
    /// The index is per user, not global: the same install GUID under a second user is a second device
    /// row, which is what lets one machine sign in as two different people.
    /// </summary>
    [Fact]
    public async Task SameDeviceId_ForDifferentUsers_IsAllowed()
    {
        var first = await CreateUserAsync();
        var second = await CreateUserAsync();
        var deviceId = Guid.NewGuid().ToString();

        using (var writeContext = fixture.CreateContext())
        {
            writeContext.Devices.Add(new Device { UserId = first.Id, DeviceId = deviceId });
            writeContext.Devices.Add(new Device { UserId = second.Id, DeviceId = deviceId });
            await writeContext.SaveChangesAsync();
        }

        using var readContext = fixture.CreateContext();
        Assert.Equal(2, await readContext.Devices.CountAsync(d => d.DeviceId == deviceId));
    }

    /// <summary>
    /// Asserts on the Postgres error code rather than on <see cref="DbUpdateException"/> alone, which
    /// would also be thrown by a row that failed for any other reason — a bad timestamp, a missing
    /// column — and so would pass without the unique index being what refused it.
    /// </summary>
    private static async Task AssertUniqueViolationAsync(WorkLedgerDbContext context)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
    }

    private async Task<User> CreateUserAsync()
    {
        using var context = fixture.CreateContext();
        var user = new User { DisplayName = $"test-{Guid.NewGuid()}" };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }
}
