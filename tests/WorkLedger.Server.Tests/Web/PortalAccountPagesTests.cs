using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data.Entities;
using WorkLedger.Server.Tests.Auth;

namespace WorkLedger.Server.Tests.Web;

/// <summary>
/// The self-service account pages (S11 items 12–15). Each has to have exactly the effect on desktop
/// installs that the S8 JSON route it mirrors has, so the tests hold real bearer tokens for the account
/// and check them after every action — with the stamp cache at its default and warmed first, so a
/// missing eviction shows up as a token that keeps working.
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public class PortalAccountPagesTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string Sam = "sam@example.com";
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a brand new passphrase";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- home -------------------------------------------------------------------------------

    [Fact]
    public async Task Home_ShowsTheAccountAndLinksToEachPage_AndTheAdminLinkOnlyForAnAdmin()
    {
        await SeedUserAsync(Sam, displayName: "Sam Lee");
        await SeedUserAsync("admin@example.com", isAdmin: true);
        using var host = PortalHost();

        var regular = await (await (await SignedInAsync(host, Sam)).GetAsync("/portal")).Content.ReadAsStringAsync();
        var admin = await (await (await SignedInAsync(host, "admin@example.com")).GetAsync("/portal")).Content.ReadAsStringAsync();

        Assert.Contains(Sam, regular);
        Assert.Contains("Sam Lee", regular);
        Assert.Contains("href=\"/portal/account/devices\"", regular);
        Assert.Contains("href=\"/portal/account/security\"", regular);
        Assert.Contains("href=\"/portal/account/delete\"", regular);
        Assert.DoesNotContain("href=\"/portal/admin\"", regular);
        Assert.Contains("href=\"/portal/admin\"", admin);
    }

    // ---- devices ----------------------------------------------------------------------------

    [Fact]
    public async Task Devices_ListsTheInstalls_WithNoCurrentDeviceMarker_AndSaysRemovingKeepsTheData()
    {
        await SeedUserAsync(Sam);
        using var host = PortalHost();
        await DeviceTokenAsync(host, Sam, "device-a", "Work laptop");
        await DeviceTokenAsync(host, Sam, "device-b", "Home desktop");
        var browser = await SignedInAsync(host, Sam);

        var html = await (await browser.GetAsync("/portal/account/devices")).Content.ReadAsStringAsync();

        Assert.Contains("Work laptop", html);
        Assert.Contains("Home desktop", html);
        Assert.DoesNotContain("This device", html);
        Assert.Contains("does not delete any of your data", html);
    }

    [Fact]
    public async Task RemovingADevice_SignsThatInstallOut_AndLeavesItsSyncedRowsAlone()
    {
        var samId = await SeedUserAsync(Sam);
        using var host = PortalHost();
        var removed = Bearer(host, await DeviceTokenAsync(host, Sam, "device-a", "Work laptop"));
        var kept = Bearer(host, await DeviceTokenAsync(host, Sam, "device-b", "Home desktop"));
        Assert.Equal(HttpStatusCode.OK, (await removed.GetAsync("/auth/devices")).StatusCode);
        await SeedProjectAsync(samId, originDeviceId: "device-a");
        var browser = await SignedInAsync(host, Sam);

        var response = await browser.SubmitAsync(
            "/portal/account/devices?handler=Remove", ("deviceId", "device-a"), ("label", "Work laptop"));

        Assert.Contains("Work laptop has been signed out and removed", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await removed.GetAsync("/auth/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await kept.GetAsync("/auth/devices")).StatusCode);
        using var db = fixture.CreateDbContext();
        Assert.True(await db.Projects.AnyAsync(p => p.UserId == samId && p.OriginDeviceId == "device-a"));
    }

    /// <summary>The device id comes from a form field, and a crafted one must never reach another account.</summary>
    [Fact]
    public async Task RemovingAnotherUsersDevice_IsNotPossible()
    {
        await SeedUserAsync(Sam);
        await SeedUserAsync("alex@example.com");
        using var host = PortalHost();
        var alexDevice = Bearer(host, await DeviceTokenAsync(host, "alex@example.com", "alex-device", "Alex's laptop"));
        var browser = await SignedInAsync(host, Sam);

        var response = await browser.SubmitAsync("/portal/account/devices?handler=Remove", ("deviceId", "alex-device"));

        Assert.Contains("no longer on your account", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await alexDevice.GetAsync("/auth/devices")).StatusCode);
    }

    // ---- password and sign-out-everywhere ---------------------------------------------------

    [Fact]
    public async Task ChangingThePassword_SignsOutEveryDevice_ButKeepsThisSession()
    {
        await SeedUserAsync(Sam);
        using var host = PortalHost();
        var device = Bearer(host, await DeviceTokenAsync(host, Sam, "device-a", "Laptop"));
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/auth/devices")).StatusCode);
        var browser = await SignedInAsync(host, Sam);

        var response = await ChangePasswordAsync(browser, Password, NewPassword, NewPassword);

        Assert.Contains("Your password has been changed", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/auth/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DeviceLoginAsync(host, Sam, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeviceLoginAsync(host, Sam, Password)).StatusCode);
    }

    /// <summary>
    /// The page that answers a password change carries forms whose antiforgery tokens were minted before
    /// the session was re-issued with its new stamp. They must still be accepted — which only holds while
    /// antiforgery identifies the user by something that does not rotate.
    /// </summary>
    [Fact]
    public async Task AfterAStampRotation_TheFormsAlreadyOnThePageStillWork()
    {
        await SeedUserAsync(Sam);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Sam);

        var changed = await ChangePasswordAsync(browser, Password, NewPassword, NewPassword);
        var tokenFromThatPage = PortalBrowser.TokenFrom(await changed.Content.ReadAsStringAsync());
        var next = await browser.PostAsync("/portal/account/security?handler=SignOutEverywhere", tokenFromThatPage);

        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Contains("Every desktop install has been signed out", await next.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal")).StatusCode);
    }

    [Fact]
    public async Task AWrongCurrentPassword_IsAMessage_ChangesNothing_AndCountsNothing()
    {
        var samId = await SeedUserAsync(Sam);
        using var host = PortalHost();
        var device = Bearer(host, await DeviceTokenAsync(host, Sam, "device-a", "Laptop"));
        var browser = await SignedInAsync(host, Sam);
        var stamp = (await UserAsync(samId)).SecurityStamp;

        var wrong = await ChangePasswordAsync(browser, "not my password at all", NewPassword, NewPassword);
        var mismatch = await ChangePasswordAsync(browser, Password, NewPassword, NewPassword + "!");
        var tooShort = await ChangePasswordAsync(browser, Password, "short", "short");

        Assert.Contains("current password is incorrect", await wrong.Content.ReadAsStringAsync());
        Assert.Contains("don&#x27;t match", await mismatch.Content.ReadAsStringAsync());
        Assert.Contains("at least", await tooShort.Content.ReadAsStringAsync());
        var user = await UserAsync(samId);
        Assert.Equal(stamp, user.SecurityStamp);
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/auth/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal")).StatusCode);
    }

    [Fact]
    public async Task SignOutEverywhere_SignsOutEveryDevice_ButKeepsThisSession_AndThePassword()
    {
        await SeedUserAsync(Sam);
        using var host = PortalHost();
        var device = Bearer(host, await DeviceTokenAsync(host, Sam, "device-a", "Laptop"));
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/auth/devices")).StatusCode);
        var browser = await SignedInAsync(host, Sam);

        var response = await browser.SubmitAsync("/portal/account/security?handler=SignOutEverywhere");

        Assert.Contains("Every desktop install has been signed out", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/auth/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DeviceLoginAsync(host, Sam, Password)).StatusCode);
    }

    // ---- delete -----------------------------------------------------------------------------

    [Fact]
    public async Task DeletingTheAccount_NeedsBothThePasswordAndTheTypedEmail()
    {
        var samId = await SeedUserAsync(Sam);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Sam);

        var wrongPassword = await DeleteAsync(browser, "not my password at all", Sam);
        var wrongEmail = await DeleteAsync(browser, Password, "someone@example.com");

        Assert.Contains("password is incorrect", await wrongPassword.Content.ReadAsStringAsync());
        Assert.Contains("doesn&#x27;t match", await wrongEmail.Content.ReadAsStringAsync());
        using var db = fixture.CreateDbContext();
        Assert.True(await db.Users.AnyAsync(u => u.Id == samId));
    }

    [Fact]
    public async Task DeletingTheAccount_EmptiesEveryTableForThatUser_AndLeavesAnotherUserAlone()
    {
        var samId = await SeedUserAsync(Sam);
        var alexId = await SeedUserAsync("alex@example.com");
        using var host = PortalHost();
        var samDevice = Bearer(host, await DeviceTokenAsync(host, Sam, "device-a", "Laptop"));
        var alexDevice = Bearer(host, await DeviceTokenAsync(host, "alex@example.com", "device-x", "Alex"));
        Assert.Equal(HttpStatusCode.OK, (await samDevice.GetAsync("/auth/devices")).StatusCode);
        await SeedProjectAsync(samId, originDeviceId: "device-a", withTaskAndEntry: true);
        await SeedProjectAsync(alexId, originDeviceId: "device-x", withTaskAndEntry: true);
        var browser = await SignedInAsync(host, Sam);

        var response = await DeleteAsync(browser, Password, "  SAM@EXAMPLE.COM ");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/portal/deleted", response.Headers.Location!.OriginalString);
        Assert.Contains(
            "all its synced data have been deleted",
            await (await browser.GetAsync("/portal/deleted")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/portal")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await samDevice.GetAsync("/auth/devices")).StatusCode);

        using var db = fixture.CreateDbContext();
        Assert.False(await db.Users.AnyAsync(u => u.Id == samId));
        Assert.False(await db.Devices.AnyAsync(d => d.UserId == samId));
        Assert.False(await db.Projects.AnyAsync(p => p.UserId == samId));
        Assert.False(await db.TaskItems.AnyAsync(t => t.UserId == samId));
        Assert.False(await db.TimeEntries.AnyAsync(e => e.UserId == samId));
        Assert.True(await db.Projects.AnyAsync(p => p.UserId == alexId));
        Assert.True(await db.TimeEntries.AnyAsync(e => e.UserId == alexId));
        Assert.Equal(HttpStatusCode.OK, (await alexDevice.GetAsync("/auth/devices")).StatusCode);
    }

    /// <summary>
    /// Allowed, as on <c>DELETE /auth/account</c> — but warned about first, and the server can recover: the
    /// setup page starts offering the promote-yourself step again.
    /// </summary>
    [Fact]
    public async Task TheLastAdmin_IsWarned_ButMayDeleteTheirAccount_AndSetupReopens()
    {
        await SeedUserAsync("admin@example.com", isAdmin: true);
        await SeedUserAsync(Sam);
        using var host = PortalHost();
        var admin = await SignedInAsync(host, "admin@example.com");

        var page = await (await admin.GetAsync("/portal/account/delete")).Content.ReadAsStringAsync();
        var response = await DeleteAsync(admin, Password, "admin@example.com");

        Assert.Contains("only administrator", page);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var setup = await (await PortalBrowser.Open(host).GetAsync("/portal/setup")).Content.ReadAsStringAsync();
        Assert.Contains("Become administrator", setup);
    }

    [Fact]
    public async Task ARegularUser_IsNotWarnedAboutBeingTheLastAdmin()
    {
        await SeedUserAsync("admin@example.com", isAdmin: true);
        await SeedUserAsync(Sam);
        using var host = PortalHost();

        var page = await (await (await SignedInAsync(host, Sam)).GetAsync("/portal/account/delete")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("only administrator", page);
    }

    // ---- shared page rules ------------------------------------------------------------------

    [Theory]
    [InlineData("/portal/account/devices?handler=Remove")]
    [InlineData("/portal/account/security?handler=Password")]
    [InlineData("/portal/account/security?handler=SignOutEverywhere")]
    [InlineData("/portal/account/delete")]
    public async Task APostWithoutTheAntiforgeryToken_Is400_AndChangesNothing(string action)
    {
        var samId = await SeedUserAsync(Sam);
        using var host = PortalHost();
        await DeviceTokenAsync(host, Sam, "device-a", "Laptop");
        var browser = await SignedInAsync(host, Sam);
        var stamp = (await UserAsync(samId)).SecurityStamp;

        var response = await browser.PostAsync(
            action, null,
            ("deviceId", "device-a"), ("currentPassword", Password), ("newPassword", NewPassword),
            ("confirmPassword", NewPassword), ("password", Password), ("confirmEmail", Sam));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var db = fixture.CreateDbContext();
        var user = await db.Users.SingleAsync(u => u.Id == samId);
        Assert.Equal(stamp, user.SecurityStamp);
        Assert.True(await db.Devices.AnyAsync(d => d.UserId == samId));
    }

    [Theory]
    [InlineData("/portal/account/devices")]
    [InlineData("/portal/account/security")]
    [InlineData("/portal/account/delete")]
    public async Task TheAccountPages_NeedAPortalSession(string page)
    {
        using var host = PortalHost();

        var response = await PortalBrowser.Open(host).GetAsync(page);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/portal/login", response.Headers.Location!.OriginalString);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private WebApplicationFactory<Program> PortalHost() =>
        fixture.CreateHost(
            ("Web:Enabled", "true"),
            ("Web:PublicBaseUrl", "https://ledger.example.test"),
            ("Auth:LoginRateLimitPerMinute", "200"));

    private static async Task<PortalBrowser> SignedInAsync(WebApplicationFactory<Program> host, string email)
    {
        var browser = PortalBrowser.Open(host);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.SignInAsync(email, Password)).StatusCode);
        return browser;
    }

    private static Task<HttpResponseMessage> ChangePasswordAsync(
        PortalBrowser browser, string current, string next, string confirm) =>
        browser.SubmitAsync(
            "/portal/account/security?handler=Password",
            ("currentPassword", current), ("newPassword", next), ("confirmPassword", confirm));

    private static Task<HttpResponseMessage> DeleteAsync(PortalBrowser browser, string password, string confirmEmail) =>
        browser.SubmitAsync("/portal/account/delete", ("password", password), ("confirmEmail", confirmEmail));

    private async Task<int> SeedUserAsync(string email, bool isAdmin = false, string? displayName = null)
    {
        using var db = fixture.CreateDbContext();
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = new PasswordService().Hash(Password),
            DisplayName = displayName,
            IsAdmin = isAdmin,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task SeedProjectAsync(int userId, string originDeviceId, bool withTaskAndEntry = false)
    {
        using var db = fixture.CreateDbContext();
        var now = DateTime.UtcNow;
        var project = new Project
        {
            UserId = userId,
            SyncId = Guid.NewGuid(),
            Version = 1,
            Name = "Client work",
            OriginDeviceId = originDeviceId,
            CreatedAt = now,
        };
        db.Projects.Add(project);
        if (withTaskAndEntry)
        {
            var task = new TaskItem
            {
                UserId = userId,
                SyncId = Guid.NewGuid(),
                ProjectSyncId = project.SyncId,
                Version = 1,
                Name = "Write the report",
                OriginDeviceId = originDeviceId,
                CreatedAt = now,
            };
            db.TaskItems.Add(task);
            db.TimeEntries.Add(new TimeEntry
            {
                UserId = userId,
                SyncId = Guid.NewGuid(),
                TaskSyncId = task.SyncId,
                Version = 1,
                StartTime = now.AddHours(-1),
                EndTime = now,
                DurationSeconds = 3600,
                OriginDeviceId = originDeviceId,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<User> UserAsync(int userId)
    {
        using var db = fixture.CreateDbContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    private static Task<HttpResponseMessage> DeviceLoginAsync(
        WebApplicationFactory<Program> host, string email, string password, string deviceId = "device-a", string? label = null) =>
        host.CreateClient().PostAsJsonAsync("/auth/login", new LoginRequest(email, password, deviceId, label));

    private static async Task<string> DeviceTokenAsync(
        WebApplicationFactory<Program> host, string email, string deviceId, string label)
    {
        var response = await DeviceLoginAsync(host, email, Password, deviceId, label);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private static HttpClient Bearer(WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
