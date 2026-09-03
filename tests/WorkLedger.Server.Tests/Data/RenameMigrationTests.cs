using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Tests.Data;

/// <summary>
/// The one test that would have caught the migration EF actually scaffolded. Left as generated,
/// <c>RenameAccountToUserWithCredentials</c> dropped the <c>Accounts</c> table and created an empty
/// <c>Users</c> one, which on a live self-hosted box would have taken every project, task, time entry
/// and enrolled device with it through the cascading foreign keys.
/// <para>
/// So this seeds the <b>pre-S6 schema</b> — migrating only as far as <c>InitialCreate</c> and writing
/// through the old <c>Accounts</c>/<c>AccountId</c> names in raw SQL, because the C# model no longer has
/// those names to write through — then migrates the rest of the way and reads the same rows back under
/// the renamed columns. Its own container, because it is the only test that needs a database mid-history
/// rather than fully migrated.
/// </para>
/// </summary>
public class RenameMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("workledger")
        .WithUsername("workledger")
        .WithPassword("workledger")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public async Task MigratingPreS6Data_PreservesEveryRowUnderTheRenamedColumns()
    {
        var projectSyncId = Guid.NewGuid();
        var taskSyncId = Guid.NewGuid();
        var entrySyncId = Guid.NewGuid();
        const string deviceId = "3f6b8e2a-pre-s6-laptop";

        using (var db = CreateContext())
        {
            await db.Database.GetService<IMigrator>().MigrateAsync("InitialCreate");

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Accounts" ("Name", "CreatedAt") VALUES ('Default', now());
                INSERT INTO "Devices" ("AccountId", "DeviceId", "Label", "CreatedAt", "LastSeenUtc")
                    SELECT "Id", {0}, 'laptop', now(), now() FROM "Accounts";
                INSERT INTO "Projects" ("AccountId", "SyncId", "Version", "IsDeleted", "CreatedAt", "UpdatedAt", "Name")
                    SELECT "Id", {1}, 3, false, now(), now(), 'Legacy work' FROM "Accounts";
                INSERT INTO "TaskItems" ("AccountId", "SyncId", "ProjectSyncId", "Version", "IsDeleted", "IsCompleted", "IsArchived", "CreatedAt", "UpdatedAt", "Name")
                    SELECT "Id", {2}, {1}, 2, false, false, false, now(), now(), 'Legacy task' FROM "Accounts";
                INSERT INTO "TimeEntries" ("AccountId", "SyncId", "TaskSyncId", "Version", "IsDeleted", "CreatedAt", "UpdatedAt", "StartTime", "EndTime", "DurationSeconds")
                    SELECT "Id", {3}, {2}, 1, false, now(), now(), now(), now(), 3600 FROM "Accounts";
                """,
                deviceId, projectSyncId, taskSyncId, entrySyncId);
        }

        using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        using (var db = CreateContext())
        {
            var user = await db.Users.SingleAsync();

            // Account.Name became User.DisplayName, and the credential columns arrive empty — this row is
            // exactly the unclaimed legacy tenant POST /auth/claim exists for.
            Assert.Equal("Default", user.DisplayName);
            Assert.Null(user.Email);
            Assert.Null(user.NormalizedEmail);
            Assert.Null(user.PasswordHash);
            Assert.False(string.IsNullOrWhiteSpace(user.SecurityStamp));

            Assert.Equal(deviceId, (await db.Devices.SingleAsync(d => d.UserId == user.Id)).DeviceId);

            var project = await db.Projects.SingleAsync(p => p.UserId == user.Id);
            Assert.Equal(projectSyncId, project.SyncId);
            Assert.Equal("Legacy work", project.Name);
            Assert.Equal(3, project.Version);

            var task = await db.TaskItems.SingleAsync(t => t.UserId == user.Id);
            Assert.Equal(taskSyncId, task.SyncId);
            Assert.Equal(projectSyncId, task.ProjectSyncId);

            var entry = await db.TimeEntries.SingleAsync(t => t.UserId == user.Id);
            Assert.Equal(entrySyncId, entry.SyncId);
            Assert.Equal(taskSyncId, entry.TaskSyncId);
            Assert.Equal(3600, entry.DurationSeconds);
        }
    }

    /// <summary>
    /// Postgres carries constraint names through a table rename, so without the explicit renames the PK
    /// and foreign keys would still be called <c>PK_Accounts</c> / <c>FK_..._Accounts_AccountId</c> while
    /// EF's model expects the <c>_Users_UserId</c> names — a mismatch nothing notices until a later
    /// migration tries to drop one by its conventional name.
    /// </summary>
    [Fact]
    public async Task MigratingRenamesTheConstraintsToTheNamesEfExpects()
    {
        using (var db = CreateContext())
        {
            await db.Database.GetService<IMigrator>().MigrateAsync("InitialCreate");
            await db.Database.MigrateAsync();
        }

        using var read = CreateContext();
        var connection = read.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT conname FROM pg_constraint
            WHERE conname LIKE 'PK_%' OR conname LIKE 'FK_%'
            ORDER BY conname
            """;

        var names = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }

        Assert.Contains("PK_Users", names);
        Assert.Contains("FK_Devices_Users_UserId", names);
        Assert.Contains("FK_Projects_Users_UserId", names);
        Assert.Contains("FK_TaskItems_Users_UserId", names);
        Assert.Contains("FK_TimeEntries_Users_UserId", names);
        Assert.DoesNotContain(names, name => name.Contains("Account", StringComparison.Ordinal));
    }

    private WorkLedgerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WorkLedgerDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        return new WorkLedgerDbContext(options);
    }
}
