using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Tests.Auth;

/// <summary>
/// S10's operator surface: who becomes an admin, what the <c>admin</c> policy admits, and the six
/// routes behind it.
/// <para>
/// Needs <see cref="UserAccountsFixture"/>'s private database rather than the shared one, and for a
/// reason beyond the usual: several assertions here are about the <i>whole database</i> — "the first
/// user on an empty database is an admin", "this deployment has no admin yet", "the last admin cannot
/// go" — and none of those can be asserted against a database another suite is adding users to.
/// </para>
/// <para>
/// <b>The stamp cache is left at its default 30s throughout, deliberately.</b> Every claim here that a
/// promotion, demotion or suspension lands on the <i>next request</i> is only a real claim while the
/// cache is on: with <c>Auth:StampCacheSeconds=0</c> these tests would pass with every cache eviction
/// in <c>AdminEndpoints</c> deleted. The tests that care warm the cache with a successful request
/// first, so the entry being dropped is demonstrably one that was there.
/// </para>
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public class AdminEndpointTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string AdminEmail = "operator@example.com";
    private const string UserEmail = "member@example.com";
    private const string OtherEmail = "someone.else@example.com";
    private const string Password = "correct horse battery staple";
    private const string WrongPassword = "correct horse battery stapler";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Who becomes an admin ---------------------------------------------------------------

    /// <summary>
    /// The rule that makes a fresh deployment administrable with no seed, no configuration and no
    /// bootstrap call — and the fact that it applies to the <i>first</i> registration only.
    /// </summary>
    [Fact]
    public async Task Register_OnAnEmptyDatabase_MakesTheFirstUserAnAdmin_AndNoOneElse()
    {
        using var host = Host();

        await RegisterAsync(host, AdminEmail);
        await RegisterAsync(host, UserEmail);

        using var db = fixture.CreateDbContext();
        var users = await db.Users.OrderBy(u => u.Id).ToListAsync();
        Assert.Equal([true, false], users.Select(u => u.IsAdmin));
    }

    /// <summary>
    /// The upgrade path. A database that already held users when S10 landed has none of them flagged,
    /// so the surface is unreachable until the operator proves themselves with the secret they already
    /// have — and then the door shuts.
    /// </summary>
    [Fact]
    public async Task Bootstrap_PromotesTheCaller_OnlyWhileTheServerHasNoAdmin()
    {
        // Seeded directly, as a pre-S10 deployment's rows would be: real users, none of them admin.
        await SeedUserAsync(AdminEmail);
        await SeedUserAsync(UserEmail);
        using var host = Host();
        var operatorToken = await SignInAsync(host, AdminEmail, "operator-laptop");

        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized(host, operatorToken).GetAsync("/admin/users")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await BootstrapAsync(host, operatorToken)).StatusCode);

        // Immediately, not after the cache TTL — the caller's own snapshot has to be evicted too.
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, operatorToken).GetAsync("/admin/users")).StatusCode);

        // Self-disarming: the second caller cannot let themselves in the same way.
        var memberToken = await SignInAsync(host, UserEmail, "member-laptop");
        Assert.Equal(HttpStatusCode.Conflict, (await BootstrapAsync(host, memberToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized(host, memberToken).GetAsync("/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Bootstrap_WithTheWrongSecret_Returns401_AndPromotesNobody()
    {
        await SeedUserAsync(AdminEmail);
        using var host = Host();
        var token = await SignInAsync(host, AdminEmail, "operator-laptop");

        var response = await BootstrapAsync(host, token, secret: "not-the-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(await IsAdminAsync(AdminEmail));
    }

    // ---- What the policy admits -------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/admin/users")]
    [InlineData("POST", "/admin/users/1/unlock")]
    [InlineData("POST", "/admin/users/1/disable")]
    [InlineData("POST", "/admin/users/1/enable")]
    [InlineData("POST", "/admin/users/1/admin")]
    [InlineData("DELETE", "/admin/users/1/admin")]
    public async Task AdminRoutes_AreForbiddenForANonAdmin_AndUnauthorizedWithoutAToken(
        string method, string path)
    {
        await SeedUserAsync(AdminEmail, isAdmin: true);
        await SeedUserAsync(UserEmail);
        using var host = Host();
        var memberToken = await SignInAsync(host, UserEmail, "member-laptop");

        var anonymous = await host.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var member = await Authorized(host, memberToken)
            .SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
    }

    /// <summary>
    /// The point of resolving <c>IsAdmin</c> from the database on every request rather than minting it
    /// into a year-long token: the privilege can be handed over and taken back, and both land on the
    /// very next request that token makes.
    /// </summary>
    [Fact]
    public async Task PromotingAndDemoting_TakeEffectOnTheNextRequest_WithTheSameToken()
    {
        var adminId = await SeedUserAsync(AdminEmail, isAdmin: true);
        var memberId = await SeedUserAsync(UserEmail);
        using var host = Host();
        var adminToken = await SignInAsync(host, AdminEmail, "operator-laptop");
        var memberToken = await SignInAsync(host, UserEmail, "member-laptop");

        // Warm the cache, so the entry the endpoints evict is demonstrably one that existed.
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized(host, memberToken).GetAsync("/admin/users")).StatusCode);

        await AssertNoContent(Authorized(host, adminToken).PostAsync($"/admin/users/{memberId}/admin", null));
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, memberToken).GetAsync("/admin/users")).StatusCode);

        await AssertNoContent(Authorized(host, adminToken).DeleteAsync($"/admin/users/{memberId}/admin"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized(host, memberToken).GetAsync("/admin/users")).StatusCode);

        // The admin's own token was never reissued through any of this.
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, adminToken).GetAsync("/admin/users")).StatusCode);
        Assert.NotEqual(adminId, memberId);
    }

    // ---- GET /admin/users -------------------------------------------------------------------

    [Fact]
    public async Task ListUsers_ReportsEveryUser_WithDeviceCountsAndState()
    {
        await SeedUserAsync(AdminEmail, isAdmin: true);
        var memberId = await SeedUserAsync(UserEmail);
        using var host = Host();
        var adminToken = await SignInAsync(host, AdminEmail, "operator-laptop");
        await SignInAsync(host, UserEmail, "member-laptop");
        await SignInAsync(host, UserEmail, "member-phone");
        await AssertNoContent(Authorized(host, adminToken).PostAsync($"/admin/users/{memberId}/disable", null));

        var users = (await Authorized(host, adminToken)
            .GetFromJsonAsync<List<AdminUserSummary>>("/admin/users"))!;

        Assert.Equal([AdminEmail, UserEmail], users.Select(u => u.Email));
        var admin = users[0];
        var member = users[1];
        Assert.True(admin.IsAdmin);
        Assert.Equal(1, admin.DeviceCount);
        Assert.False(admin.IsDisabled);
        Assert.False(member.IsAdmin);
        Assert.Equal(2, member.DeviceCount);
        Assert.True(member.IsDisabled);
        Assert.False(member.IsLocked);
        Assert.NotNull(member.LastLoginUtc);
    }

    [Fact]
    public async Task AdminRoutes_ForAUserIdThatMatchesNobody_Return404()
    {
        await SeedUserAsync(AdminEmail, isAdmin: true);
        using var host = Host();
        var token = await SignInAsync(host, AdminEmail, "operator-laptop");

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Authorized(host, token).PostAsync("/admin/users/999999/unlock", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Authorized(host, token).PostAsync("/admin/users/999999/disable", null)).StatusCode);
    }

    // ---- Disable / enable -------------------------------------------------------------------

    /// <summary>
    /// Suspension has to close both doors — the token someone is already holding, and the login that
    /// would mint a new one — and re-opening it has to restore both. The login refusal is asserted to be
    /// the <i>same</i> 401 a wrong password gets, because a distinguishable one would be a fresh
    /// account-enumeration channel on the one anonymous endpoint that goes out of its way not to be one.
    /// </summary>
    [Fact]
    public async Task Disable_RevokesExistingTokensAndLogins_AndEnableRestoresBoth()
    {
        await SeedUserAsync(AdminEmail, isAdmin: true);
        var memberId = await SeedUserAsync(UserEmail);
        using var host = Host();
        var adminToken = await SignInAsync(host, AdminEmail, "operator-laptop");
        var memberToken = await SignInAsync(host, UserEmail, "member-laptop");

        // Warm the cache with a request that works, so the eviction below has something to drop.
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, memberToken).GetAsync("/auth/whoami")).StatusCode);

        await AssertNoContent(Authorized(host, adminToken).PostAsync($"/admin/users/{memberId}/disable", null));

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await Authorized(host, memberToken).GetAsync("/auth/whoami")).StatusCode);

        var suspended = await LoginAsync(host, UserEmail, Password, "member-laptop");
        var wrongPassword = await LoginAsync(host, UserEmail, WrongPassword, "member-laptop");
        Assert.Equal(HttpStatusCode.Unauthorized, suspended.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(), await suspended.Content.ReadAsStringAsync());

        await AssertNoContent(Authorized(host, adminToken).PostAsync($"/admin/users/{memberId}/enable", null));

        // The token was only ever refused, never destroyed — so it works again, immediately.
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, memberToken).GetAsync("/auth/whoami")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(host, UserEmail, Password, "member-laptop")).StatusCode);
    }

    [Fact]
    public async Task Disable_ThenDisableAgain_IsANoOp204()
    {
        await SeedUserAsync(AdminEmail, isAdmin: true);
        var memberId = await SeedUserAsync(UserEmail);
        using var host = Host();
        var token = await SignInAsync(host, AdminEmail, "operator-laptop");

        await AssertNoContent(Authorized(host, token).PostAsync($"/admin/users/{memberId}/disable", null));
        var first = await DisabledAtAsync(memberId);
        await AssertNoContent(Authorized(host, token).PostAsync($"/admin/users/{memberId}/disable", null));

        Assert.Equal(first, await DisabledAtAsync(memberId));
    }

    // ---- The last-admin guard ---------------------------------------------------------------

    /// <summary>
    /// The guard that stops the deployment being rendered unadministrable.
    /// <para>
    /// <b>Only self-demotion can reach it</b>, and that is why self-demotion is allowed: the caller is
    /// always an active admin, so a target who is somebody else means there are at least two admins and
    /// the count can never be one. Applying the self-action guard to demotion as well would make this
    /// check unreachable — the invariant would then rest entirely on that guard, untested and
    /// unstated.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheLastAdmin_CannotStandDown_AndNothingIsWritten()
    {
        var soleAdminId = await SeedUserAsync(AdminEmail, isAdmin: true);
        await SeedUserAsync(UserEmail);
        using var host = Host();
        var token = await SignInAsync(host, AdminEmail, "operator-laptop");

        var response = await Authorized(host, token).DeleteAsync($"/admin/users/{soleAdminId}/admin");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("last administrator", await response.Content.ReadAsStringAsync());
        Assert.True(await IsAdminAsync(AdminEmail));
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, token).GetAsync("/admin/users")).StatusCode);
    }

    /// <summary>
    /// The same call once a second admin exists: allowed, and it lands immediately — the caller's own
    /// next request is a <c>403</c>, which is the cache eviction doing its job on the actor's own key.
    /// </summary>
    [Fact]
    public async Task AnAdmin_CanStandDown_OnceSomeoneElseCanAdminister()
    {
        var firstId = await SeedUserAsync(AdminEmail, isAdmin: true);
        var secondId = await SeedUserAsync(UserEmail, isAdmin: true);
        using var host = Host();
        var token = await SignInAsync(host, AdminEmail, "operator-laptop");

        await AssertNoContent(Authorized(host, token).DeleteAsync($"/admin/users/{firstId}/admin"));

        Assert.False(await IsAdminAsync(AdminEmail));
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized(host, token).GetAsync("/admin/users")).StatusCode);

        // And the one left standing cannot then leave either.
        var secondToken = await SignInAsync(host, UserEmail, "member-laptop");
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await Authorized(host, secondToken).DeleteAsync($"/admin/users/{secondId}/admin")).StatusCode);
    }

    /// <summary>
    /// Suspending or deleting the <i>other</i> admin is allowed and leaves the caller as the only one —
    /// the invariant is "at least one", not "at least two".
    /// </summary>
    [Fact]
    public async Task RemovingTheOtherAdmin_IsAllowed_AndLeavesTheCallerAsTheOnlyOne()
    {
        await SeedUserAsync(AdminEmail, isAdmin: true);
        var otherId = await SeedUserAsync(OtherEmail, isAdmin: true);
        using var host = Host();
        var token = await SignInAsync(host, AdminEmail, "operator-laptop");

        await AssertNoContent(Authorized(host, token).PostAsync($"/admin/users/{otherId}/disable", null));

        Assert.NotNull(await DisabledAtAsync(otherId));
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, token).GetAsync("/admin/users")).StatusCode);
    }

    // ---- The self-action guard --------------------------------------------------------------

    /// <summary>
    /// Suspending or deleting yourself through these routes is refused: they act on <i>other</i> people,
    /// and an admin who wants to leave has <c>DELETE /auth/account</c>, which re-verifies their password.
    /// The delete refusal names it rather than merely refusing. Demotion is the deliberate exception —
    /// see the last-admin tests above.
    /// </summary>
    [Fact]
    public async Task AnAdmin_CannotSuspendOrDeleteThemselves_ThroughTheAdminRoutes()
    {
        var selfId = await SeedUserAsync(AdminEmail, isAdmin: true);
        await SeedUserAsync(OtherEmail, isAdmin: true);
        using var host = Host();
        var token = await SignInAsync(host, AdminEmail, "operator-laptop");

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await Authorized(host, token).PostAsync($"/admin/users/{selfId}/disable", null)).StatusCode);

        var delete = await DeleteUserAsync(host, token, selfId, AdminEmail);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Contains("DELETE /auth/account", await delete.Content.ReadAsStringAsync());

        Assert.Null(await DisabledAtAsync(selfId));
        Assert.True(await ExistsAsync(selfId));
        Assert.True(await IsAdminAsync(AdminEmail));
    }

    // ---- Unlock -----------------------------------------------------------------------------

    /// <summary>
    /// The endpoint that pays for the file: a locked-out user gets back in without anyone opening
    /// <c>psql</c>. Asserted <i>inside</i> the window that would still have been in force, which is what
    /// distinguishes a real unlock from a test that simply waited the lockout out.
    /// </summary>
    [Fact]
    public async Task Unlock_LetsALockedOutUserSignInAgain_WithinTheWindowThatWouldStillHaveApplied()
    {
        await SeedUserAsync(AdminEmail, isAdmin: true);
        var memberId = await SeedUserAsync(UserEmail);
        using var host = Host(("Auth:MaxFailedLogins", "2"), ("Auth:LockoutMinutes", "60"));
        var adminToken = await SignInAsync(host, AdminEmail, "operator-laptop");

        await LoginAsync(host, UserEmail, WrongPassword, "member-laptop");
        var locked = await LoginAsync(host, UserEmail, WrongPassword, "member-laptop");
        Assert.Equal(HttpStatusCode.Locked, locked.StatusCode);

        // The correct password is refused while the window stands — that is the lockout working.
        Assert.Equal(
            HttpStatusCode.Locked,
            (await LoginAsync(host, UserEmail, Password, "member-laptop")).StatusCode);

        var listed = (await Authorized(host, adminToken)
            .GetFromJsonAsync<List<AdminUserSummary>>("/admin/users"))!;
        Assert.True(listed.Single(u => u.Id == memberId).IsLocked);

        await AssertNoContent(Authorized(host, adminToken).PostAsync($"/admin/users/{memberId}/unlock", null));

        // A 60-minute window has certainly not elapsed, so this can only be the unlock.
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(host, UserEmail, Password, "member-laptop")).StatusCode);
    }

    [Fact]
    public async Task Unlock_OnAUserWhoWasNotLocked_IsANoOp204()
    {
        await SeedUserAsync(AdminEmail, isAdmin: true);
        var memberId = await SeedUserAsync(UserEmail);
        using var host = Host();
        var token = await SignInAsync(host, AdminEmail, "operator-laptop");

        await AssertNoContent(Authorized(host, token).PostAsync($"/admin/users/{memberId}/unlock", null));
    }

    // ---- DELETE /admin/users/{id} -----------------------------------------------------------

    [Fact]
    public async Task DeleteUser_WithAMismatchedConfirmEmail_Returns400_AndDeletesNothing()
    {
        await SeedUserAsync(AdminEmail, isAdmin: true);
        var memberId = await SeedUserAsync(UserEmail);
        await SeedDataAsync(memberId);
        using var host = Host();
        var token = await SignInAsync(host, AdminEmail, "operator-laptop");

        var response = await DeleteUserAsync(host, token, memberId, OtherEmail);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await ExistsAsync(memberId));
        using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.Projects.CountAsync(p => p.UserId == memberId));
    }

    /// <summary>
    /// The scoping assertion that actually proves an admin delete is a delete of <i>one</i> account:
    /// a second user with data of its own has to come through untouched.
    /// </summary>
    [Fact]
    public async Task DeleteUser_EmptiesEveryTableForThatUser_RefusesTheirToken_AndLeavesOthersAlone()
    {
        await SeedUserAsync(AdminEmail, isAdmin: true);
        var memberId = await SeedUserAsync(UserEmail);
        var bystanderId = await SeedUserAsync(OtherEmail);
        await SeedDataAsync(memberId);
        await SeedDataAsync(bystanderId);
        using var host = Host();
        var adminToken = await SignInAsync(host, AdminEmail, "operator-laptop");
        var memberToken = await SignInAsync(host, UserEmail, "member-laptop");

        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, memberToken).GetAsync("/auth/whoami")).StatusCode);

        await AssertNoContent(DeleteUserAsync(host, adminToken, memberId, UserEmail));

        using var db = fixture.CreateDbContext();
        Assert.False(await db.Users.AnyAsync(u => u.Id == memberId));
        Assert.Equal(0, await db.Devices.CountAsync(d => d.UserId == memberId));
        Assert.Equal(0, await db.Projects.CountAsync(p => p.UserId == memberId));
        Assert.Equal(0, await db.TaskItems.CountAsync(t => t.UserId == memberId));
        Assert.Equal(0, await db.TimeEntries.CountAsync(e => e.UserId == memberId));

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await Authorized(host, memberToken).GetAsync("/auth/whoami")).StatusCode);

        Assert.True(await db.Users.AnyAsync(u => u.Id == bystanderId));
        Assert.Equal(1, await db.Projects.CountAsync(p => p.UserId == bystanderId));
        Assert.Equal(1, await db.TaskItems.CountAsync(t => t.UserId == bystanderId));
        Assert.Equal(3, await db.TimeEntries.CountAsync(e => e.UserId == bystanderId));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private WebApplicationFactory<Program> Host(params (string Key, string? Value)[] overrides) =>
        fixture.CreateHost([("Auth:LoginRateLimitPerMinute", "200"), .. overrides]);

    private async Task<int> SeedUserAsync(string email, bool isAdmin = false)
    {
        using var db = fixture.CreateDbContext();
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = new PasswordService().Hash(Password),
            IsAdmin = isAdmin,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task SeedDataAsync(int userId)
    {
        using var db = fixture.CreateDbContext();
        var now = DateTime.UtcNow;
        var project = new Project
        {
            UserId = userId,
            SyncId = Guid.NewGuid(),
            Version = 1,
            Name = "Client work",
            OriginDeviceId = "seed-device",
            CreatedAt = now,
        };
        var task = new TaskItem
        {
            UserId = userId,
            SyncId = Guid.NewGuid(),
            ProjectSyncId = project.SyncId,
            Version = 1,
            Name = "Write the report",
            OriginDeviceId = "seed-device",
            CreatedAt = now,
        };
        db.Projects.Add(project);
        db.TaskItems.Add(task);
        db.TimeEntries.AddRange(Enumerable.Range(0, 3).Select(i => new TimeEntry
        {
            UserId = userId,
            SyncId = Guid.NewGuid(),
            TaskSyncId = task.SyncId,
            Version = 1,
            StartTime = now.AddHours(-i - 1),
            EndTime = now.AddHours(-i).AddMinutes(-30),
            DurationSeconds = 1800,
            OriginDeviceId = "seed-device",
            CreatedAt = now,
        }));
        await db.SaveChangesAsync();
    }

    private static async Task RegisterAsync(WebApplicationFactory<Program> host, string email)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Bootstrap-Secret", TestConfig.BootstrapSecret);
        var response = await client.PostAsJsonAsync(
            "/auth/register", new RegisterUserRequest(email, Password, null));
        response.EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> LoginAsync(
        WebApplicationFactory<Program> host, string email, string password, string deviceId) =>
        host.CreateClient().PostAsJsonAsync("/auth/login", new LoginRequest(email, password, deviceId, null));

    private static async Task<string> SignInAsync(
        WebApplicationFactory<Program> host, string email, string deviceId)
    {
        var response = await LoginAsync(host, email, Password, deviceId);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private static Task<HttpResponseMessage> BootstrapAsync(
        WebApplicationFactory<Program> host, string token, string? secret = null)
    {
        var client = Authorized(host, token);
        client.DefaultRequestHeaders.Add("X-Bootstrap-Secret", secret ?? TestConfig.BootstrapSecret);
        return client.PostAsync("/admin/bootstrap", null);
    }

    /// <summary><c>HttpClient.DeleteAsync</c> takes no body, and this endpoint's guard lives in one.</summary>
    private static Task<HttpResponseMessage> DeleteUserAsync(
        WebApplicationFactory<Program> host, string token, int userId, string confirmEmail)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/users/{userId}")
        {
            Content = JsonContent.Create(new AdminDeleteUserRequest(confirmEmail)),
        };
        return Authorized(host, token).SendAsync(request);
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task AssertNoContent(Task<HttpResponseMessage> call)
    {
        var response = await call;
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<bool> IsAdminAsync(string email)
    {
        using var db = fixture.CreateDbContext();
        return (await db.Users.SingleAsync(u => u.NormalizedEmail == email.ToUpperInvariant())).IsAdmin;
    }

    private async Task<DateTime?> DisabledAtAsync(int userId)
    {
        using var db = fixture.CreateDbContext();
        return (await db.Users.SingleAsync(u => u.Id == userId)).DisabledAtUtc;
    }

    private async Task<bool> ExistsAsync(int userId)
    {
        using var db = fixture.CreateDbContext();
        return await db.Users.AnyAsync(u => u.Id == userId);
    }
}
