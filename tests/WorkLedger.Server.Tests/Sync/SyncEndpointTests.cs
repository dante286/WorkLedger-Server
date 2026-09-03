using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Payloads;
using WorkLedger.Contracts.Sync;
using WorkLedger.Server.Data.Entities;
using WorkLedger.Server.Tests.Auth;

namespace WorkLedger.Server.Tests.Sync;

/// <summary>
/// Devices under one shared user, exercised through the real HTTP surface against a real Postgres.
/// Tests share that user, so each uses fresh Guids and takes its pull cursor from a baseline pull
/// rather than assuming an empty database.
/// <para>
/// The user is created here rather than seeded by the host (S6 seeds nothing) and devices are enrolled
/// through the fixture rather than through <c>POST /auth/login</c> — see
/// <see cref="AuthWebAppFixture"/> for why. Every merge and scope assertion below is unchanged from S3;
/// only where the token comes from has moved.
/// </para>
/// </summary>
[Collection(nameof(AuthCollection))]
public class SyncEndpointTests(AuthWebAppFixture fixture)
{
    /// <summary>
    /// Deliberately Unspecified-kind: SQLite does not round-trip DateTimeKind, so this is the shape
    /// timestamps actually arrive in from the client, and Npgsql throws on anything but Utc.
    /// </summary>
    private static readonly DateTime EntityCreatedAt = new(2026, 1, 15, 9, 30, 0, DateTimeKind.Unspecified);

    [Fact]
    public async Task Push_WithoutToken_Returns401()
    {
        var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/sync/push", new SyncPushRequest([]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Pull_WithoutToken_Returns401()
    {
        var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/sync/pull");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Pull_WithUnparseableSince_Returns400()
    {
        var device = await NewDeviceAsync();

        var response = await device.Client.GetAsync("/sync/pull?since=not-a-timestamp");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Push_NewCreate_InsertsRowScopedToTheUser()
    {
        var device = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        var push = await PushAsync(device, ProjectChange(syncId, device.DeviceId, version: 1, SyncOperations.Create));

        Assert.True(push.Results.Single().Accepted);

        using var db = fixture.CreateDbContext();
        var row = await db.Projects.SingleAsync(p => p.SyncId == syncId);
        Assert.Equal(1, row.Version);
        Assert.Equal(device.DeviceId, row.OriginDeviceId);
        Assert.False(row.IsDeleted);
        Assert.True(row.UserId > 0);
        Assert.Equal("Project", row.Name);

        // Rule H: an Unspecified-kind timestamp is stored as the same instant in UTC, not shifted
        // through the server's local zone.
        Assert.Equal(DateTimeKind.Utc, row.CreatedAt.Kind);
        Assert.Equal(EntityCreatedAt, row.CreatedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Push_HigherVersion_Overwrites()
    {
        var author = await NewDeviceAsync();
        var editor = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        await PushAsync(author, ProjectChange(syncId, author.DeviceId, version: 1, SyncOperations.Create));
        var push = await PushAsync(editor, ProjectChange(syncId, editor.DeviceId, version: 2, name: "Renamed"));

        Assert.True(push.Results.Single().Accepted);
        Assert.Equal(2, push.Results.Single().AuthoritativeVersion);

        using var db = fixture.CreateDbContext();
        var row = await db.Projects.SingleAsync(p => p.SyncId == syncId);
        Assert.Equal("Renamed", row.Name);
        Assert.Equal(editor.DeviceId, row.OriginDeviceId);
    }

    [Fact]
    public async Task Push_EqualVersionFromAnotherDevice_ServerWinsAndReturnsAuthoritativePayload()
    {
        var author = await NewDeviceAsync();
        var rival = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        await PushAsync(author, ProjectChange(syncId, author.DeviceId, version: 3, name: "Server copy"));
        var push = await PushAsync(rival, ProjectChange(syncId, rival.DeviceId, version: 3, name: "Loser copy"));

        var result = push.Results.Single();
        Assert.False(result.Accepted);
        Assert.Equal(3, result.AuthoritativeVersion);
        Assert.NotNull(result.AuthoritativePayloadJson);

        var authoritative = JsonSerializer.Deserialize<ProjectPayload>(result.AuthoritativePayloadJson!, SyncJson.Options);
        Assert.Equal("Server copy", authoritative!.Name);

        // The payload has to say what it is; the winning row here is alive.
        Assert.Equal(SyncOperations.Update, result.AuthoritativeOperation);

        using var db = fixture.CreateDbContext();
        Assert.Equal("Server copy", (await db.Projects.SingleAsync(p => p.SyncId == syncId)).Name);
    }

    /// <summary>
    /// WL-98. The winning row can be a tombstone, and the payload alone cannot say so — the payload
    /// DTOs carry no IsDeleted. A client that guessed "update" here would resurrect the entity
    /// locally and permanently, and no later pull would correct it: rule E means this branch writes
    /// nothing, so the winner's UpdatedAt stays behind the client's cursor.
    /// </summary>
    [Fact]
    public async Task Push_WhenTheWinningRowIsATombstone_SaysSoInAuthoritativeOperation()
    {
        var author = await NewDeviceAsync();
        var rival = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        // Author creates then deletes: the tombstone lands at version 2 (the delete's bump).
        await PushAsync(author, ProjectChange(syncId, author.DeviceId, version: 1, SyncOperations.Create));
        await PushAsync(author, ProjectChange(syncId, author.DeviceId, version: 1, SyncOperations.Delete));

        // The rival never saw the delete and pushes an update at the tombstone's version.
        var push = await PushAsync(rival, ProjectChange(syncId, rival.DeviceId, version: 2, name: "Resurrected"));

        var result = push.Results.Single();
        Assert.False(result.Accepted);
        Assert.Equal(2, result.AuthoritativeVersion);
        Assert.NotNull(result.AuthoritativePayloadJson);
        Assert.Equal(SyncOperations.Delete, result.AuthoritativeOperation);

        using var db = fixture.CreateDbContext();
        Assert.True((await db.Projects.SingleAsync(p => p.SyncId == syncId)).IsDeleted);
    }

    [Fact]
    public async Task Push_WhenAnItemFailsValidation_CarriesNoAuthoritativePayloadOrOperation()
    {
        var device = await NewDeviceAsync();

        var push = await PushAsync(
            device,
            NewChange("sasquatch", Guid.NewGuid(), SyncOperations.Create, "{}", 1, device.DeviceId));

        var result = push.Results.Single();
        Assert.False(result.Accepted);

        // A null payload is how the client tells "your change was invalid" apart from "my row won,
        // here it is" — so the operation must be absent too.
        Assert.Null(result.AuthoritativePayloadJson);
        Assert.Null(result.AuthoritativeOperation);
    }

    [Fact]
    public async Task Push_WhenAccepted_CarriesNoAuthoritativeOperation()
    {
        var device = await NewDeviceAsync();

        var push = await PushAsync(
            device,
            ProjectChange(Guid.NewGuid(), device.DeviceId, version: 1, SyncOperations.Create));

        var result = push.Results.Single();
        Assert.True(result.Accepted);
        Assert.Null(result.AuthoritativeOperation);
    }

    [Fact]
    public async Task Push_LowerVersionFromAnotherDevice_ServerWins()
    {
        var author = await NewDeviceAsync();
        var rival = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        await PushAsync(author, ProjectChange(syncId, author.DeviceId, version: 5, name: "Server copy"));
        var push = await PushAsync(rival, ProjectChange(syncId, rival.DeviceId, version: 2, name: "Stale copy"));

        Assert.False(push.Results.Single().Accepted);
        Assert.Equal(5, push.Results.Single().AuthoritativeVersion);
    }

    /// <summary>
    /// Rule E is not "don't bump the version", it is "don't write at all": UpdatedAt is the pull
    /// cursor, so an incidental EF write re-broadcasts the row to every other device. This is the
    /// test that catches that, and it checks both halves — the column, and the observable effect.
    /// </summary>
    [Fact]
    public async Task Push_WhenServerWins_DoesNotRestampUpdatedAtOrReDeliverTheRow()
    {
        var author = await NewDeviceAsync();
        var rival = await NewDeviceAsync();
        var observer = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        await PushAsync(author, ProjectChange(syncId, author.DeviceId, version: 4, name: "Server copy"));

        DateTime updatedAtBefore;
        using (var db = fixture.CreateDbContext())
        {
            updatedAtBefore = (await db.Projects.SingleAsync(p => p.SyncId == syncId)).UpdatedAt;
        }

        // The observer drains what exists so far, then we replay a losing change and a pure replay.
        var cursor = (await PullAsync(observer)).ServerTime;
        await PushAsync(rival, ProjectChange(syncId, rival.DeviceId, version: 4, name: "Loser copy"));
        await PushAsync(author, ProjectChange(syncId, author.DeviceId, version: 4, name: "Server copy"));

        using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(updatedAtBefore, (await db.Projects.SingleAsync(p => p.SyncId == syncId)).UpdatedAt);
        }

        var pull = await PullAsync(observer, cursor);
        Assert.DoesNotContain(pull.Changes, c => c.EntityId == syncId);
    }

    [Fact]
    public async Task Push_ReplayOfAppliedChange_IsAcceptedWithoutBumpingTheVersion()
    {
        var device = await NewDeviceAsync();
        var syncId = Guid.NewGuid();
        var change = ProjectChange(syncId, device.DeviceId, version: 2, SyncOperations.Create);

        await PushAsync(device, change);
        var replay = await PushAsync(device, change);

        // Accepted, so the client retires the queue row instead of re-pushing it forever.
        Assert.True(replay.Results.Single().Accepted);
        Assert.Equal(2, replay.Results.Single().AuthoritativeVersion);

        using var db = fixture.CreateDbContext();
        Assert.Equal(2, (await db.Projects.SingleAsync(p => p.SyncId == syncId)).Version);
    }

    /// <summary>
    /// Rule D. The client queues a delete at the same version as the last update (WL-93), so this is
    /// the shape every real delete arrives in — if it were rejected, deletes would never propagate.
    /// </summary>
    [Fact]
    public async Task Push_DeleteAtSameVersion_TombstonesWithABumpAndPropagatesToTheOtherDevice()
    {
        var author = await NewDeviceAsync();
        var observer = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        await PushAsync(author, ProjectChange(syncId, author.DeviceId, version: 2, SyncOperations.Create));
        var cursor = (await PullAsync(observer)).ServerTime;

        var push = await PushAsync(author, ProjectChange(syncId, author.DeviceId, version: 2, SyncOperations.Delete));

        var result = push.Results.Single();
        Assert.True(result.Accepted);
        Assert.Equal(3, result.AuthoritativeVersion);

        using (var db = fixture.CreateDbContext())
        {
            var row = await db.Projects.SingleAsync(p => p.SyncId == syncId);
            Assert.True(row.IsDeleted);
            Assert.Equal(3, row.Version);
        }

        var pull = await PullAsync(observer, cursor);
        var change = Assert.Single(pull.Changes, c => c.EntityId == syncId);
        Assert.Equal(SyncOperations.Delete, change.Operation);

        // The bumped version is what makes the tombstone beat the other device's copy: its merge
        // rule is a strict >, so an unbumped tombstone would be dropped on arrival.
        Assert.Equal(3, change.Version);
    }

    [Fact]
    public async Task Push_DeleteForUnknownEntity_InsertsATombstone()
    {
        var device = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        var push = await PushAsync(device, ProjectChange(syncId, device.DeviceId, version: 1, SyncOperations.Delete));

        Assert.True(push.Results.Single().Accepted);

        using var db = fixture.CreateDbContext();
        Assert.True((await db.Projects.SingleAsync(p => p.SyncId == syncId)).IsDeleted);
    }

    [Fact]
    public async Task Push_ReplayedDelete_DoesNotBumpTheVersionAgain()
    {
        var device = await NewDeviceAsync();
        var syncId = Guid.NewGuid();
        var delete = ProjectChange(syncId, device.DeviceId, version: 2, SyncOperations.Delete);

        await PushAsync(device, ProjectChange(syncId, device.DeviceId, version: 2, SyncOperations.Create));
        await PushAsync(device, delete);
        var replay = await PushAsync(device, delete);

        Assert.True(replay.Results.Single().Accepted);

        using var db = fixture.CreateDbContext();
        Assert.Equal(3, (await db.Projects.SingleAsync(p => p.SyncId == syncId)).Version);
    }

    [Fact]
    public async Task Pull_ExcludesTheCallersOwnWrites()
    {
        var author = await NewDeviceAsync();
        var observer = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        var authorCursor = (await PullAsync(author)).ServerTime;
        var observerCursor = (await PullAsync(observer)).ServerTime;

        await PushAsync(author, ProjectChange(syncId, author.DeviceId, version: 1, SyncOperations.Create));

        Assert.DoesNotContain((await PullAsync(author, authorCursor)).Changes, c => c.EntityId == syncId);
        Assert.Contains((await PullAsync(observer, observerCursor)).Changes, c => c.EntityId == syncId);
    }

    [Fact]
    public async Task Pull_NeverReturnsAnotherUsersData()
    {
        var device = await NewDeviceAsync();
        var foreignSyncId = Guid.NewGuid();

        using (var db = fixture.CreateDbContext())
        {
            var foreignUser = new User { DisplayName = "Someone else" };
            db.Users.Add(foreignUser);
            await db.SaveChangesAsync();

            db.Projects.Add(new Project
            {
                UserId = foreignUser.Id,
                SyncId = foreignSyncId,
                Version = 1,
                OriginDeviceId = Guid.NewGuid().ToString(),
                Name = "Confidential",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // No cursor at all: the widest possible pull, which is exactly when a missing user filter
        // would show up.
        var pull = await PullAsync(device);

        Assert.DoesNotContain(pull.Changes, c => c.EntityId == foreignSyncId);
    }

    /// <summary>
    /// The end-to-end mirror of the client's RunSyncAsync_TwoDevicesWithCollidingLocalIds
    /// regression: two devices that assigned the same local autoincrement Id to unrelated entities
    /// must survive as two distinct rows, because identity is the SyncId and nothing else.
    /// </summary>
    [Fact]
    public async Task Push_TwoDevicesWithCollidingLocalIdsButDistinctSyncIds_BothSurvive()
    {
        var first = await NewDeviceAsync();
        var second = await NewDeviceAsync();
        var firstSyncId = Guid.NewGuid();
        var secondSyncId = Guid.NewGuid();

        var firstCursor = (await PullAsync(first)).ServerTime;
        var secondCursor = (await PullAsync(second)).ServerTime;

        // Both devices call this "local Id 1" — a collision that is invisible on the wire and must
        // stay invisible in the database.
        await PushAsync(first, ProjectChange(firstSyncId, first.DeviceId, version: 1, SyncOperations.Create, "First device project"));
        await PushAsync(second, ProjectChange(secondSyncId, second.DeviceId, version: 1, SyncOperations.Create, "Second device project"));

        using (var db = fixture.CreateDbContext())
        {
            var rows = await db.Projects.Where(p => p.SyncId == firstSyncId || p.SyncId == secondSyncId).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(2, rows.Select(r => r.Id).Distinct().Count());
        }

        var firstPull = await PullAsync(first, firstCursor);
        Assert.Contains(firstPull.Changes, c => c.EntityId == secondSyncId);
        Assert.DoesNotContain(firstPull.Changes, c => c.EntityId == firstSyncId);

        var secondPull = await PullAsync(second, secondCursor);
        Assert.Contains(secondPull.Changes, c => c.EntityId == firstSyncId);
        Assert.DoesNotContain(secondPull.Changes, c => c.EntityId == secondSyncId);
    }

    [Fact]
    public async Task Pull_ServerTimeFedBackAsSince_DoesNotReDeliverTheSameChanges()
    {
        var author = await NewDeviceAsync();
        var observer = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        var cursor = (await PullAsync(observer)).ServerTime;
        await PushAsync(author, ProjectChange(syncId, author.DeviceId, version: 1, SyncOperations.Create));

        var first = await PullAsync(observer, cursor);
        Assert.Contains(first.Changes, c => c.EntityId == syncId);

        var second = await PullAsync(observer, first.ServerTime);
        Assert.DoesNotContain(second.Changes, c => c.EntityId == syncId);
    }

    [Fact]
    public async Task Pull_WithNoSince_ReturnsEverythingForTheUser()
    {
        var author = await NewDeviceAsync();
        var observer = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        await PushAsync(author, ProjectChange(syncId, author.DeviceId, version: 1, SyncOperations.Create));

        // A client that has never synced (or has restarted — its cursor is in-memory, WL-92) sends
        // no cursor and must get the full history rather than an error.
        Assert.Contains((await PullAsync(observer)).Changes, c => c.EntityId == syncId);
    }

    [Fact]
    public async Task Pull_PayloadIsPascalCase()
    {
        var author = await NewDeviceAsync();
        var observer = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        var cursor = (await PullAsync(observer)).ServerTime;
        await PushAsync(author, ProjectChange(syncId, author.DeviceId, version: 1, SyncOperations.Create));

        var change = Assert.Single((await PullAsync(observer, cursor)).Changes, c => c.EntityId == syncId);

        // Asserted on the raw payload string, not on a deserialized object: the client reads
        // payloads case-sensitively, so camelCase would deserialize to an empty entity without
        // throwing, and a round-trip assertion through a case-insensitive reader would pass anyway.
        Assert.Contains("\"Name\"", change.PayloadJson);
        Assert.Contains("\"SyncId\"", change.PayloadJson);
        Assert.DoesNotContain("\"name\"", change.PayloadJson);
    }

    [Fact]
    public async Task Push_AppliesParentsBeforeChildrenAndAnswersInRequestOrder()
    {
        var device = await NewDeviceAsync();
        var projectSyncId = Guid.NewGuid();
        var taskSyncId = Guid.NewGuid();
        var timeEntrySyncId = Guid.NewGuid();

        // Deliberately child-first: the server reorders internally but must answer positionally.
        var changes = new[]
        {
            TimeEntryChange(timeEntrySyncId, taskSyncId, device.DeviceId, version: 1, SyncOperations.Create),
            TaskChange(taskSyncId, projectSyncId, device.DeviceId, version: 1, SyncOperations.Create),
            ProjectChange(projectSyncId, device.DeviceId, version: 1, SyncOperations.Create),
        };

        var push = await PushAsync(device, changes);

        Assert.All(push.Results, r => Assert.True(r.Accepted));
        Assert.Equal(changes.Select(c => c.EntityId), push.Results.Select(r => r.EntityId));

        using var db = fixture.CreateDbContext();
        Assert.Equal(projectSyncId, (await db.TaskItems.SingleAsync(t => t.SyncId == taskSyncId)).ProjectSyncId);
        Assert.Equal(taskSyncId, (await db.TimeEntries.SingleAsync(t => t.SyncId == timeEntrySyncId)).TaskSyncId);
    }

    /// <summary>
    /// One batch, several changes for one entity — the case that makes EntityId unusable as a result
    /// key (rule F) and that a non-stable sort would silently invert.
    /// </summary>
    [Fact]
    public async Task Push_MultipleChangesForOneEntity_AreAppliedInOrderAndAckedPositionally()
    {
        var device = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        // The delete carries the entity as its payload, as the client's Delete*Async does — so it
        // carries the *edited* name, not the original.
        var push = await PushAsync(
            device,
            ProjectChange(syncId, device.DeviceId, version: 1, SyncOperations.Create),
            ProjectChange(syncId, device.DeviceId, version: 2, name: "Edited"),
            ProjectChange(syncId, device.DeviceId, version: 2, SyncOperations.Delete, "Edited"));

        Assert.Equal(3, push.Results.Count);
        Assert.All(push.Results, r => Assert.Equal(syncId, r.EntityId));
        Assert.All(push.Results, r => Assert.True(r.Accepted));

        using var db = fixture.CreateDbContext();
        var row = await db.Projects.SingleAsync(p => p.SyncId == syncId);

        // Ordering preserved: had the sort inverted create/update/delete, this would be alive.
        Assert.True(row.IsDeleted);
        Assert.Equal("Edited", row.Name);
        Assert.Equal(3, row.Version);
    }

    [Fact]
    public async Task Push_PerItemValidationFailures_DoNotFailTheRestOfTheBatch()
    {
        var device = await NewDeviceAsync();
        var goodSyncId = Guid.NewGuid();
        var unknownTypeSyncId = Guid.NewGuid();
        var malformedSyncId = Guid.NewGuid();
        var orphanTaskSyncId = Guid.NewGuid();

        var changes = new[]
        {
            ProjectChange(goodSyncId, device.DeviceId, version: 1, SyncOperations.Create),
            NewChange("sasquatch", unknownTypeSyncId, SyncOperations.Create, "{}", 1, device.DeviceId),
            NewChange(SyncEntityTypes.Project, malformedSyncId, SyncOperations.Create, "{not json", 1, device.DeviceId),
            TaskChange(orphanTaskSyncId, Guid.Empty, device.DeviceId, version: 1, SyncOperations.Create),
        };

        var push = await PushAsync(device, changes);

        Assert.True(push.Results[0].Accepted);
        Assert.False(push.Results[1].Accepted);
        Assert.Contains("Unknown EntityType", push.Results[1].Reason);
        Assert.False(push.Results[2].Accepted);
        Assert.Contains("PayloadJson", push.Results[2].Reason);
        Assert.False(push.Results[3].Accepted);
        Assert.Contains("ProjectSyncId", push.Results[3].Reason);

        using var db = fixture.CreateDbContext();
        Assert.True(await db.Projects.AnyAsync(p => p.SyncId == goodSyncId));
        Assert.False(await db.Projects.AnyAsync(p => p.SyncId == malformedSyncId));
        Assert.False(await db.TaskItems.AnyAsync(t => t.SyncId == orphanTaskSyncId));
    }

    [Fact]
    public async Task Push_VersionBelowOne_FailsThatItem()
    {
        var device = await NewDeviceAsync();
        var syncId = Guid.NewGuid();

        var push = await PushAsync(device, ProjectChange(syncId, device.DeviceId, version: 0, SyncOperations.Create));

        Assert.False(push.Results.Single().Accepted);
        Assert.Contains("Version", push.Results.Single().Reason);
    }

    private sealed record TestDevice(string DeviceId, HttpClient Client);

    /// <summary>
    /// The user every device in this class enrolls under, created once. Devices must share a user for
    /// the cross-device assertions to mean anything: an author and an observer on different users would
    /// pass a pull test by tenancy isolation rather than by the merge working.
    /// </summary>
    private static readonly SemaphoreSlim SharedUserGate = new(1, 1);
    private static int _sharedUserId;

    private async Task<TestDevice> NewDeviceAsync()
    {
        await SharedUserGate.WaitAsync();
        try
        {
            if (_sharedUserId == 0)
            {
                _sharedUserId = await fixture.NewUserAsync("sync");
            }
        }
        finally
        {
            SharedUserGate.Release();
        }

        var device = await fixture.EnrollDeviceAsync(_sharedUserId, "sync-test");
        return new TestDevice(device.DeviceId, device.Client);
    }

    private static async Task<SyncPushResponse> PushAsync(TestDevice device, params SyncChangeDto[] changes)
    {
        var response = await device.Client.PostAsJsonAsync("/sync/push", new SyncPushRequest(changes));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SyncPushResponse>())!;
    }

    private static async Task<SyncPullResponse> PullAsync(TestDevice device, DateTime? since = null)
    {
        var url = since is null
            ? "/sync/pull"
            : $"/sync/pull?since={Uri.EscapeDataString(since.Value.ToString("o"))}";

        var response = await device.Client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SyncPullResponse>())!;
    }

    private static SyncChangeDto ProjectChange(
        Guid syncId, string deviceId, int version, string operation = SyncOperations.Update, string name = "Project")
    {
        var payload = new ProjectPayload(syncId, name, "CODE", "Description", EntityCreatedAt, null, version, deviceId);
        return NewChange(SyncEntityTypes.Project, syncId, operation, Serialize(payload), version, deviceId);
    }

    private static SyncChangeDto TaskChange(
        Guid syncId, Guid projectSyncId, string deviceId, int version, string operation = SyncOperations.Update)
    {
        var payload = new TaskItemPayload(
            syncId, projectSyncId, "Task", "Notes", false, false, EntityCreatedAt, null, version, deviceId);
        return NewChange(SyncEntityTypes.Task, syncId, operation, Serialize(payload), version, deviceId);
    }

    private static SyncChangeDto TimeEntryChange(
        Guid syncId, Guid taskSyncId, string deviceId, int version, string operation = SyncOperations.Update)
    {
        var payload = new TimeEntryPayload(
            syncId, taskSyncId, EntityCreatedAt, EntityCreatedAt.AddHours(1), 3600, EntityCreatedAt, null, version, deviceId);
        return NewChange(SyncEntityTypes.TimeEntry, syncId, operation, Serialize(payload), version, deviceId);
    }

    private static SyncChangeDto NewChange(
        string entityType, Guid syncId, string operation, string? payloadJson, int version, string deviceId)
        => new(entityType, syncId, operation, payloadJson, version, deviceId, DateTime.UtcNow);

    private static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, SyncJson.Options);
}
