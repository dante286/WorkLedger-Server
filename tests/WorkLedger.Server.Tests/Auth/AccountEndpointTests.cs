using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Auth;
using WorkLedger.Contracts.Payloads;
using WorkLedger.Contracts.Sync;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Tests.Auth;

/// <summary>
/// S8's four endpoints: <c>GET /auth/devices</c>, <c>DELETE /auth/devices/{deviceId}</c>,
/// <c>POST /auth/logout-all</c> and <c>DELETE /auth/account</c>.
/// <para>
/// These need <see cref="UserAccountsFixture"/>'s private database rather than the shared one, for two
/// reasons the other auth suites share and one of their own: deletion asserts that <i>every table</i> is
/// empty for a user and that a second user's rows survived, which is an answer about the whole database;
/// and a per-test host means a fresh <c>IMemoryCache</c>, without which one test's cached revocation
/// snapshots would decide the next one's.
/// </para>
/// <para>
/// <b>The stamp cache is left at its default 30s on purpose.</b> Every revocation assertion here is that
/// the kill is <i>immediate</i>, and that is only a real claim while the cache is on: with
/// <c>Auth:StampCacheSeconds=0</c> these tests would pass without any of the eviction code existing. The
/// tests that care warm the cache with a successful request first, so the entry being dropped is one
/// that was demonstrably there.
/// </para>
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public class AccountEndpointTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string Email = "owner@example.com";
    private const string Password = "correct horse battery staple";
    private const string WrongPassword = "correct horse battery stapler";
    private const string OtherEmail = "someone.else@example.com";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- GET /auth/devices ------------------------------------------------------------------

    /// <summary>
    /// The scoping test that matters: a second user with devices of its own must be entirely absent,
    /// not merely ordered lower. Also pins the two things a client renders from — most recently seen
    /// first, and exactly one row flagged <see cref="DeviceSummary.IsCurrent"/>.
    /// </summary>
    [Fact]
    public async Task Devices_ListsOnlyTheCallersDevices_MostRecentFirst_AndFlagsTheCurrentOne()
    {
        await SeedUserAsync();
        var otherUserId = await SeedUserAsync(OtherEmail);
        await SeedDeviceAsync(otherUserId, "intruder-device");
        using var host = Host();

        // Signed in oldest-first, so "most recently seen" is the reverse of enrollment order.
        await SignInAsync(host, "device-a", "Desktop");
        await SignInAsync(host, "device-b", "Laptop");
        var current = await SignInAsync(host, "device-c", "Phone");

        var devices = (await Authorized(host, current)
            .GetFromJsonAsync<List<DeviceSummary>>("/auth/devices"))!;

        Assert.Equal(["device-c", "device-b", "device-a"], devices.Select(d => d.DeviceId));
        Assert.Equal(["Phone", "Laptop", "Desktop"], devices.Select(d => d.Label));
        Assert.DoesNotContain(devices, d => d.DeviceId == "intruder-device");
        Assert.Equal("device-c", Assert.Single(devices, d => d.IsCurrent).DeviceId);
        Assert.All(devices, d => Assert.NotNull(d.LastSeenUtc));
    }

    [Fact]
    public async Task Devices_WithoutAToken_Returns401()
    {
        await SeedUserAsync();
        using var host = Host();

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/auth/devices")).StatusCode);
    }

    // ---- DELETE /auth/devices/{deviceId} ----------------------------------------------------

    [Fact]
    public async Task DeleteDevice_Returns204_AndTheRowIsGone()
    {
        await SeedUserAsync();
        using var host = Host();
        var keeper = await SignInAsync(host, "device-a", "Desktop");
        await SignInAsync(host, "device-b", "Laptop");

        var response = await Authorized(host, keeper).DeleteAsync("/auth/devices/device-b");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var db = fixture.CreateDbContext();
        Assert.False(await db.Devices.AnyAsync(d => d.DeviceId == "device-b"));
        Assert.True(await db.Devices.AnyAsync(d => d.DeviceId == "device-a"));
    }

    /// <summary>
    /// The removed device's token dies on its <i>very next</i> request, not once
    /// <c>Auth:StampCacheSeconds</c> has elapsed. The first pull is not a spare assertion: it is what
    /// puts the device's snapshot in the cache, so the second pull can only be refused because the
    /// endpoint evicted it.
    /// </summary>
    [Fact]
    public async Task DeleteDevice_RevokesThatDevicesTokenImmediately_NotAfterTheCacheTtl()
    {
        await SeedUserAsync();
        using var host = Host();
        var keeper = await SignInAsync(host, "device-a", "Desktop");
        var doomed = await SignInAsync(host, "device-b", "Laptop");

        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, doomed).GetAsync("/sync/pull")).StatusCode);

        await Authorized(host, keeper).DeleteAsync("/auth/devices/device-b");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Authorized(host, doomed).GetAsync("/sync/pull")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, keeper).GetAsync("/sync/pull")).StatusCode);
    }

    /// <summary>
    /// A device id that exists but belongs to someone else is a 404 — indistinguishable from one that
    /// does not exist at all, the way the report routes answer a foreign SyncId — and the row survives.
    /// </summary>
    [Fact]
    public async Task DeleteDevice_OfAnotherUser_Returns404_AndTheRowSurvives()
    {
        await SeedUserAsync();
        var otherUserId = await SeedUserAsync(OtherEmail);
        await SeedDeviceAsync(otherUserId, "their-device");
        using var host = Host();
        var token = await SignInAsync(host, "device-a", "Desktop");

        var theirs = await Authorized(host, token).DeleteAsync("/auth/devices/their-device");
        var nobodys = await Authorized(host, token).DeleteAsync("/auth/devices/no-such-device");

        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nobodys.StatusCode);
        Assert.Equal(await theirs.Content.ReadAsStringAsync(), await nobodys.Content.ReadAsStringAsync());
        using var db = fixture.CreateDbContext();
        Assert.True(await db.Devices.AnyAsync(d => d.DeviceId == "their-device" && d.UserId == otherUserId));
    }

    /// <summary>Removing the device you are calling from is allowed, and signs that device out.</summary>
    [Fact]
    public async Task DeleteDevice_OwnCurrentDevice_Returns204ThenThatTokenIs401()
    {
        await SeedUserAsync();
        using var host = Host();
        var token = await SignInAsync(host, "device-a", "Desktop");
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, token).GetAsync("/sync/pull")).StatusCode);

        var response = await Authorized(host, token).DeleteAsync("/auth/devices/device-a");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Authorized(host, token).GetAsync("/sync/pull")).StatusCode);
    }

    /// <summary>
    /// The behaviour most likely to be "fixed" into a bug: the rows a removed device originated belong
    /// to the user, so they survive, and the device gets them all back by logging in again.
    /// </summary>
    [Fact]
    public async Task DeleteDevice_LeavesTheSyncedDataIntact_AndTheDeviceCanLogInAgain()
    {
        var userId = await SeedUserAsync();
        using var host = Host();
        var token = await SignInAsync(host, "device-a", "Desktop");
        var syncId = Guid.NewGuid();
        await PushProjectAsync(host, token, syncId, "device-a", "Quarterly report");

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Authorized(host, token).DeleteAsync("/auth/devices/device-a")).StatusCode);

        using (var db = fixture.CreateDbContext())
        {
            var project = await db.Projects.SingleAsync(p => p.UserId == userId);
            Assert.Equal(syncId, project.SyncId);
            Assert.Equal("Quarterly report", project.Name);
            Assert.Equal("device-a", project.OriginDeviceId);
        }

        // Another device still syncs the removed one's work. Asserted from device-b rather than from
        // the re-enrolled device-a because a pull deliberately does not echo a device its own changes
        // back, and the re-enrolled row keeps the same DeviceId — so device-a seeing nothing here would
        // prove nothing either way.
        var observer = await SignInAsync(host, "device-b", "Laptop");
        var pull = await Authorized(host, observer).GetFromJsonAsync<SyncPullResponse>("/sync/pull");
        Assert.Equal(syncId, Assert.Single(pull!.Changes).EntityId);

        // Same DeviceId, same user: login upserts the row back and the token it mints works.
        var reissued = await SignInAsync(host, "device-a", "Desktop");
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, reissued).GetAsync("/sync/pull")).StatusCode);
        using var reloaded = fixture.CreateDbContext();
        Assert.True(await reloaded.Devices.AnyAsync(d => d.UserId == userId && d.DeviceId == "device-a"));
    }

    // ---- POST /auth/logout-all ---------------------------------------------------------------

    /// <summary>
    /// Every other device's token dies, the caller gets a working replacement rather than logging
    /// itself out, and the device rows themselves survive — logout-all is not device removal.
    /// </summary>
    [Fact]
    public async Task LogoutAll_ReturnsAWorkingTokenForTheCaller_AndKillsEveryOtherDevicesToken()
    {
        await SeedUserAsync();
        using var host = Host();
        var caller = await SignInAsync(host, "device-a", "Desktop");
        var other = await SignInAsync(host, "device-b", "Laptop");
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, other).GetAsync("/sync/pull")).StatusCode);

        var response = await Authorized(host, caller).PostAsync("/auth/logout-all", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.Equal(Email, body!.User.Email);
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, body.Token).GetAsync("/sync/pull")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Authorized(host, other).GetAsync("/sync/pull")).StatusCode);
        // The caller's own pre-call token died with everyone else's; the response body is the way back in.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Authorized(host, caller).GetAsync("/sync/pull")).StatusCode);

        using var db = fixture.CreateDbContext();
        Assert.Equal(2, await db.Devices.CountAsync());
    }

    [Fact]
    public async Task LogoutAll_WithoutAToken_Returns401()
    {
        await SeedUserAsync();
        using var host = Host();

        var response = await host.CreateClient().PostAsync("/auth/logout-all", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- DELETE /auth/account ----------------------------------------------------------------

    [Fact]
    public async Task DeleteAccount_WrongPassword_Returns401_AndNothingIsDeleted()
    {
        var userId = await SeedUserAsync();
        using var host = Host();
        var token = await SignInAsync(host, "device-a", "Desktop");
        await PushProjectAsync(host, token, Guid.NewGuid(), "device-a", "Keep me");

        var response = await DeleteAccountAsync(host, token, WrongPassword, Email);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertUserIsFullyIntactAsync(userId, projects: 1, devices: 1);
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, token).GetAsync("/sync/pull")).StatusCode);
    }

    [Fact]
    public async Task DeleteAccount_MismatchedConfirmEmail_Returns400_AndNothingIsDeleted()
    {
        var userId = await SeedUserAsync();
        using var host = Host();
        var token = await SignInAsync(host, "device-a", "Desktop");
        await PushProjectAsync(host, token, Guid.NewGuid(), "device-a", "Keep me");

        var wrongAddress = await DeleteAccountAsync(host, token, Password, OtherEmail);
        var blank = await DeleteAccountAsync(host, token, Password, "");

        Assert.Equal(HttpStatusCode.BadRequest, wrongAddress.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        await AssertUserIsFullyIntactAsync(userId, projects: 1, devices: 1);
    }

    /// <summary>
    /// The happy path, and the test that actually proves the scoping: a second user with a device and a
    /// project of its own is untouched. The typed confirmation is sent upper-cased, which also pins the
    /// case-insensitive comparison.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_Returns204_EmptiesEveryTableForThatUser_AndLeavesASecondUserUntouched()
    {
        var userId = await SeedUserAsync();
        var survivorId = await SeedUserAsync(OtherEmail);
        await SeedDeviceAsync(survivorId, "survivor-device");
        await SeedProjectAsync(survivorId, "Survivor project");
        using var host = Host();
        var token = await SignInAsync(host, "device-a", "Desktop");
        await SeedFullTreeAsync(userId, entries: 3);

        var response = await DeleteAccountAsync(host, token, Password, Email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertNothingRemainsForAsync(userId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Authorized(host, token).GetAsync("/sync/pull")).StatusCode);

        using var db = fixture.CreateDbContext();
        Assert.True(await db.Users.AnyAsync(u => u.Id == survivorId));
        Assert.Equal(1, await db.Devices.CountAsync(d => d.UserId == survivorId));
        Assert.Equal(1, await db.Projects.CountAsync(p => p.UserId == survivorId));
    }

    /// <summary>
    /// A real account's worth of data, deleted in one request, leaving no orphan in any of the four
    /// child tables. <c>ExecuteDeleteAsync</c> is one statement per table precisely so this does not
    /// mean materializing several hundred entities first.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_WithHundredsOfEntries_CompletesAndLeavesNoOrphans()
    {
        var userId = await SeedUserAsync();
        using var host = Host();
        var token = await SignInAsync(host, "device-a", "Desktop");
        await SeedFullTreeAsync(userId, entries: 300);

        var response = await DeleteAccountAsync(host, token, Password, Email);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertNothingRemainsForAsync(userId);
    }

    [Fact]
    public async Task DeleteAccount_WithoutAToken_Returns401()
    {
        await SeedUserAsync();
        using var host = Host();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest(Password, Email)),
        };

        var response = await host.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync());
    }

    /// <summary>
    /// A push in flight when the account is deleted must not leave rows behind. Whichever order the two
    /// transactions land in, the database ends empty for this user: a push that commits first has its
    /// rows swept by the delete's <c>WHERE UserId</c>, and one that commits after the user row is gone
    /// is refused by the foreign key. The push's own status is therefore not asserted — a 200 and a 500
    /// are both correct outcomes here, and only the end state is the invariant.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_RacingASyncPush_LeavesNothingBehindForThatUser()
    {
        var userId = await SeedUserAsync();
        using var host = Host();
        var pusher = await SignInAsync(host, "device-a", "Desktop");
        var deleter = await SignInAsync(host, "device-b", "Laptop");
        await SeedFullTreeAsync(userId, entries: 25);

        // The push's failure mode is a foreign-key violation, and TestServer rethrows an unhandled
        // server exception into the caller rather than turning it into a 500 the way a socket would.
        // Swallowing it here is not hiding a defect: it *is* the losing-race outcome this test allows.
        async Task PushAsync()
        {
            try
            {
                await Authorized(host, pusher).PostAsJsonAsync(
                    "/sync/push",
                    new SyncPushRequest(Enumerable.Range(0, 25)
                        .Select(i => ProjectChange(Guid.NewGuid(), "device-a", $"Racing project {i}"))
                        .ToList()));
            }
            catch (Exception)
            {
                // The account went away underneath it.
            }
        }

        var push = PushAsync();
        var delete = DeleteAccountAsync(host, deleter, Password, Email);
        await Task.WhenAll(push, delete);

        Assert.Equal(HttpStatusCode.NoContent, (await delete).StatusCode);
        await AssertNothingRemainsForAsync(userId);
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>
    /// The rate limiter is shared with <c>/auth/login</c> and these tests sign in several devices per
    /// test; it is not what any of them is asserting on, so it is raised out of the way. Everything
    /// else — notably <c>Auth:StampCacheSeconds</c> — is left at its production default.
    /// </summary>
    private WebApplicationFactory<Program> Host() =>
        fixture.CreateHost(("Auth:LoginRateLimitPerMinute", "100"));

    private static async Task<string> SignInAsync(
        WebApplicationFactory<Program> host, string deviceId, string? label)
    {
        var response = await host.CreateClient().PostAsJsonAsync(
            "/auth/login", new LoginRequest(Email, Password, deviceId, label));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// <c>HttpClient.DeleteAsync</c> takes no body, and this endpoint's two guards live in one. A
    /// <c>DELETE</c> carrying a body is legal and minimal APIs bind it like any other complex parameter.
    /// </summary>
    private static Task<HttpResponseMessage> DeleteAccountAsync(
        WebApplicationFactory<Program> host, string token, string password, string confirmEmail)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest(password, confirmEmail)),
        };
        return Authorized(host, token).SendAsync(request);
    }

    private async Task<int> SeedUserAsync(string email = Email)
    {
        using var db = fixture.CreateDbContext();
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = new PasswordService().Hash(Password),
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task SeedDeviceAsync(int userId, string deviceId)
    {
        using var db = fixture.CreateDbContext();
        var now = DateTime.UtcNow;
        db.Devices.Add(new Device
        {
            UserId = userId,
            DeviceId = deviceId,
            Label = deviceId,
            CreatedAt = now,
            LastSeenUtc = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedProjectAsync(int userId, string name)
    {
        using var db = fixture.CreateDbContext();
        db.Projects.Add(NewProject(userId, name));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One project, one task and <paramref name="entries"/> time entries, so a deletion assertion has
    /// something in every child table rather than only the one that is easiest to write.
    /// </summary>
    private async Task SeedFullTreeAsync(int userId, int entries)
    {
        using var db = fixture.CreateDbContext();
        var now = DateTime.UtcNow;
        var project = NewProject(userId, "Client work");
        var task = new TaskItem
        {
            UserId = userId,
            SyncId = Guid.NewGuid(),
            ProjectSyncId = project.SyncId,
            Version = 1,
            Name = "Write the report",
            OriginDeviceId = "device-a",
            CreatedAt = now,
        };
        db.Projects.Add(project);
        db.TaskItems.Add(task);
        db.TimeEntries.AddRange(Enumerable.Range(0, entries).Select(i => new TimeEntry
        {
            UserId = userId,
            SyncId = Guid.NewGuid(),
            TaskSyncId = task.SyncId,
            Version = 1,
            StartTime = now.AddHours(-i - 1),
            EndTime = now.AddHours(-i).AddMinutes(-30),
            DurationSeconds = 1800,
            OriginDeviceId = "device-a",
            CreatedAt = now,
        }));
        await db.SaveChangesAsync();
    }

    private static Project NewProject(int userId, string name) => new()
    {
        UserId = userId,
        SyncId = Guid.NewGuid(),
        Version = 1,
        Name = name,
        OriginDeviceId = "seed-device",
        CreatedAt = DateTime.UtcNow,
    };

    private static async Task PushProjectAsync(
        WebApplicationFactory<Program> host, string token, Guid syncId, string deviceId, string name)
    {
        var response = await Authorized(host, token).PostAsJsonAsync(
            "/sync/push", new SyncPushRequest([ProjectChange(syncId, deviceId, name)]));
        response.EnsureSuccessStatusCode();
    }

    private static SyncChangeDto ProjectChange(Guid syncId, string deviceId, string name)
    {
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var payload = new ProjectPayload(syncId, name, "CODE", null, createdAt, null, 1, deviceId);
        return new SyncChangeDto(
            SyncEntityTypes.Project,
            syncId,
            SyncOperations.Create,
            System.Text.Json.JsonSerializer.Serialize(payload, SyncJson.Options),
            1,
            deviceId,
            createdAt);
    }

    private async Task AssertUserIsFullyIntactAsync(int userId, int projects, int devices)
    {
        using var db = fixture.CreateDbContext();
        Assert.True(await db.Users.AnyAsync(u => u.Id == userId));
        Assert.Equal(projects, await db.Projects.CountAsync(p => p.UserId == userId));
        Assert.Equal(devices, await db.Devices.CountAsync(d => d.UserId == userId));
    }

    private async Task AssertNothingRemainsForAsync(int userId)
    {
        using var db = fixture.CreateDbContext();
        Assert.False(await db.Users.AnyAsync(u => u.Id == userId));
        Assert.Equal(0, await db.Devices.CountAsync(d => d.UserId == userId));
        Assert.Equal(0, await db.Projects.CountAsync(p => p.UserId == userId));
        Assert.Equal(0, await db.TaskItems.CountAsync(t => t.UserId == userId));
        Assert.Equal(0, await db.TimeEntries.CountAsync(e => e.UserId == userId));
    }
}
