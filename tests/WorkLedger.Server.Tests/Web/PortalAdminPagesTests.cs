using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data.Entities;
using WorkLedger.Server.Tests.Auth;

namespace WorkLedger.Server.Tests.Web;

/// <summary>
/// The operator pages (S11 items 16–18): users and their actions, invites, and password-reset links.
/// <para>
/// These assert the <i>effect</i> of each action on the database and on other sessions, not just the
/// page's wording, because the point of the pages is that they run <c>AdminOperations</c> and
/// <c>InviteService</c> — the JSON routes' code. The one-time links are proven real by spending them
/// through the JSON routes, which is also the end-to-end flow group E's pages will stand on.
/// </para>
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public partial class PortalAdminPagesTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string Admin = "admin@example.com";
    private const string Sam = "sam@example.com";
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a brand new passphrase";
    private const string PublicBaseUrl = "https://ledger.example.test";
    private const string UsersPage = "/portal/admin/users";
    private const string InvitesPage = "/portal/admin/invites";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- users ------------------------------------------------------------------------------

    [Fact]
    public async Task TheUsersPage_ListsEveryUserWithTheirState()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        await SeedUserAsync("locked@example.com", locked: true);
        await SeedUserAsync("suspended@example.com", disabled: true);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);

        var html = await (await browser.GetAsync(UsersPage)).Content.ReadAsStringAsync();

        Assert.Contains(Admin, html);
        Assert.Contains("(you)", html);
        Assert.Contains("locked@example.com", html);
        Assert.Contains("Locked out", html);
        Assert.Contains("suspended@example.com", html);
        Assert.Contains("Suspended", html);
    }

    [Fact]
    public async Task Unlock_ClearsTheLockout()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var lockedId = await SeedUserAsync("locked@example.com", locked: true);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);

        var response = await ActAsync(browser, "Unlock", lockedId);

        Assert.Contains("Unlocked locked@example.com", await response.Content.ReadAsStringAsync());
        var user = await UserAsync(lockedId);
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockoutEndUtc);
    }

    /// <summary>The same eviction the JSON route runs: the suspended user's device is refused on its next request.</summary>
    [Fact]
    public async Task Suspend_SignsTheUsersDevicesOutOnTheNextRequest_AndRestoreBringsThemBack()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        using var host = PortalHost();
        var device = Bearer(host, await DeviceTokenAsync(host, Sam));
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/auth/devices")).StatusCode);
        var browser = await SignedInAsync(host, Admin);

        await ActAsync(browser, "Disable", samId);

        Assert.NotNull((await UserAsync(samId)).DisabledAtUtc);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/auth/devices")).StatusCode);

        await ActAsync(browser, "Enable", samId);

        Assert.Null((await UserAsync(samId)).DisabledAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/auth/devices")).StatusCode);
    }

    [Fact]
    public async Task PromoteAndDemote_ChangeAnotherUsersRole()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);

        await ActAsync(browser, "Promote", samId);
        Assert.True((await UserAsync(samId)).IsAdmin);

        await ActAsync(browser, "Demote", samId);
        Assert.False((await UserAsync(samId)).IsAdmin);
    }

    [Fact]
    public async Task DemotingYourself_WhileAnotherAdminExists_SendsYouToYourAccount()
    {
        var adminId = await SeedUserAsync(Admin, isAdmin: true);
        await SeedUserAsync("other-admin@example.com", isAdmin: true);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);

        var response = await ActAsync(browser, "Demote", adminId);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/portal", response.Headers.Location!.OriginalString);
        Assert.False((await UserAsync(adminId)).IsAdmin);
        Assert.StartsWith("/portal/denied", (await browser.GetAsync(UsersPage)).Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task DemotingYourself_AsTheLastAdmin_IsRefusedWithTheReason()
    {
        var adminId = await SeedUserAsync(Admin, isAdmin: true);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);

        var response = await ActAsync(browser, "Demote", adminId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("the last administrator", await response.Content.ReadAsStringAsync());
        Assert.True((await UserAsync(adminId)).IsAdmin);
    }

    [Fact]
    public async Task SuspendingOrDeletingYourself_IsRefused()
    {
        var adminId = await SeedUserAsync(Admin, isAdmin: true);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);

        var suspend = await ActAsync(browser, "Disable", adminId);
        var delete = await ActAsync(browser, "Delete", adminId, ("confirmEmail", Admin));

        Assert.Contains("can&#x27;t suspend your own account", await suspend.Content.ReadAsStringAsync());
        Assert.Contains("can&#x27;t delete your own account", await delete.Content.ReadAsStringAsync());
        var admin = await UserAsync(adminId);
        Assert.Null(admin.DisabledAtUtc);
    }

    [Fact]
    public async Task Delete_WithTheWrongEmailTyped_DeletesNothing()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);

        var mismatch = await ActAsync(browser, "Delete", samId, ("confirmEmail", "someone-else@example.com"));
        var blank = await ActAsync(browser, "Delete", samId, ("confirmEmail", ""));

        Assert.Contains("Nothing was deleted", await mismatch.Content.ReadAsStringAsync());
        Assert.Contains("Nothing was deleted", await blank.Content.ReadAsStringAsync());
        using var db = fixture.CreateDbContext();
        Assert.True(await db.Users.AnyAsync(u => u.Id == samId));
    }

    [Fact]
    public async Task Delete_WithTheEmailTyped_RemovesTheAccountAndItsData()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        await SeedProjectAsync(samId);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);

        var response = await ActAsync(browser, "Delete", samId, ("confirmEmail", "  SAM@example.com "));

        Assert.Contains("Deleted sam@example.com", await response.Content.ReadAsStringAsync());
        using var db = fixture.CreateDbContext();
        Assert.False(await db.Users.AnyAsync(u => u.Id == samId));
        Assert.False(await db.Projects.AnyAsync(p => p.UserId == samId));
    }

    [Fact]
    public async Task ARegularUser_CannotPostAnAdminAction()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        var otherId = await SeedUserAsync("other@example.com");
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Sam);
        var token = await browser.FormTokenAsync("/portal");

        var response = await browser.PostAsync($"{UsersPage}?handler=Disable", token, ("userId", otherId.ToString()));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/portal/denied", response.Headers.Location!.OriginalString);
        Assert.Null((await UserAsync(otherId)).DisabledAtUtc);
        Assert.False((await UserAsync(samId)).IsAdmin);
    }

    [Fact]
    public async Task AnAdminActionPostedWithoutTheAntiforgeryToken_Is400_AndChangesNothing()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);

        var response = await browser.PostAsync($"{UsersPage}?handler=Disable", null, ("userId", samId.ToString()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await UserAsync(samId)).DisabledAtUtc);
    }

    // ---- password reset links ---------------------------------------------------------------

    [Fact]
    public async Task AResetLink_IsShownOnce_BuiltFromThePublicBaseUrl_AndWorks()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);

        var response = await ActAsync(browser, "Reset", samId);
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("shown once", html);
        var token = ResetToken(html);
        Assert.DoesNotContain(token, await (await browser.GetAsync(UsersPage)).Content.ReadAsStringAsync());

        var redeem = await host.CreateClient()
            .PostAsJsonAsync("/auth/password/reset", new ResetPasswordRequest(token, NewPassword));
        Assert.Equal(HttpStatusCode.NoContent, redeem.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DeviceLoginAsync(host, Sam, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task IssuingASecondResetLink_KillsTheFirst()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);

        var first = ResetToken(await (await ActAsync(browser, "Reset", samId)).Content.ReadAsStringAsync());
        var second = ResetToken(await (await ActAsync(browser, "Reset", samId)).Content.ReadAsStringAsync());

        var client = host.CreateClient();
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/auth/password/reset", new ResetPasswordRequest(first, NewPassword))).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/auth/password/reset", new ResetPasswordRequest(second, NewPassword))).StatusCode);
    }

    [Fact]
    public async Task AResetForTheCredentialLessLegacyRow_IsRefusedWithTheReason()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        int legacyId;
        using (var db = fixture.CreateDbContext())
        {
            var legacy = new User { DisplayName = "Legacy", CreatedAt = DateTime.UtcNow };
            db.Users.Add(legacy);
            await db.SaveChangesAsync();
            legacyId = legacy.Id;
        }

        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);

        var html = await (await ActAsync(browser, "Reset", legacyId)).Content.ReadAsStringAsync();

        Assert.Contains("nothing to reset", html);
        Assert.DoesNotContain("/portal/reset#token=", html);
    }

    // ---- invites ----------------------------------------------------------------------------

    [Fact]
    public async Task CreatingAnInvite_ShowsItsLinkOnce_AndTheLinkRegistersExactlyOneAccount()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        using var host = PortalHost(("Auth:SignupSecretEnabled", "false"));
        var browser = await SignedInAsync(host, Admin);

        var response = await browser.SubmitAsync(
            $"{InvitesPage}?handler=Create", ("email", "New.Person@example.com"), ("expiresInDays", "3"), ("note", "for Alex"));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("shown once", html);
        var token = InviteToken(html);

        var listed = await (await browser.GetAsync(InvitesPage)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, listed);
        Assert.Contains("new.person@example.com", listed);
        Assert.Contains("for Alex", listed);

        var register = await host.CreateClient().PostAsJsonAsync(
            "/auth/register", new RegisterUserRequest("new.person@example.com", Password, "Alex", InviteToken: token));
        var again = await host.CreateClient().PostAsJsonAsync(
            "/auth/register", new RegisterUserRequest("new.person@example.com", Password, "Alex", InviteToken: token));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("Used", await (await browser.GetAsync(InvitesPage)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreatingAnInvite_WithABadEmailOrExpiry_CreatesNothing()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);

        var badEmail = await browser.SubmitAsync($"{InvitesPage}?handler=Create", ("email", "not-an-address"));
        var badDays = await browser.SubmitAsync($"{InvitesPage}?handler=Create", ("expiresInDays", "0"));
        var notANumber = await browser.SubmitAsync($"{InvitesPage}?handler=Create", ("expiresInDays", "soon"));

        Assert.Contains("doesn&#x27;t look right", await badEmail.Content.ReadAsStringAsync());
        Assert.Contains("at least one day", await badDays.Content.ReadAsStringAsync());
        Assert.Contains("whole number of days", await notANumber.Content.ReadAsStringAsync());
        using var db = fixture.CreateDbContext();
        Assert.Empty(db.Invites);
    }

    [Fact]
    public async Task RevokingAnOpenInvite_KillsItsLink_ButAUsedInviteIsKept()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        using var host = PortalHost();
        var browser = await SignedInAsync(host, Admin);
        var open = InviteToken(await (await browser.SubmitAsync($"{InvitesPage}?handler=Create")).Content.ReadAsStringAsync());
        var used = InviteToken(await (await browser.SubmitAsync($"{InvitesPage}?handler=Create")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await host.CreateClient().PostAsJsonAsync(
            "/auth/register", new RegisterUserRequest(Sam, Password, null, InviteToken: used))).StatusCode);
        int openId, usedId;
        using (var db = fixture.CreateDbContext())
        {
            openId = (await db.Invites.SingleAsync(i => i.RedeemedAtUtc == null)).Id;
            usedId = (await db.Invites.SingleAsync(i => i.RedeemedAtUtc != null)).Id;
        }

        var revoked = await browser.SubmitAsync($"{InvitesPage}?handler=Revoke", ("inviteId", openId.ToString()));
        var refused = await browser.SubmitAsync($"{InvitesPage}?handler=Revoke", ("inviteId", usedId.ToString()));

        Assert.Contains("Invite revoked", await revoked.Content.ReadAsStringAsync());
        Assert.Contains("already been used", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().PostAsJsonAsync(
            "/auth/register", new RegisterUserRequest("late@example.com", Password, null, InviteToken: open))).StatusCode);
        using var after = fixture.CreateDbContext();
        Assert.Equal(usedId, (await after.Invites.SingleAsync()).Id);
    }

    // ---- logs -------------------------------------------------------------------------------

    /// <summary>
    /// Every admin action writes the same audit line the JSON routes do, and no one-time token reaches
    /// the log at any point in its life — issued here, spent through the JSON routes.
    /// </summary>
    [Fact]
    public async Task AdminActionsAreAudited_AndNoTokenEverReachesTheLog()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        var logs = new CapturedLogs();
        using var baseHost = PortalHost();
        using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureLogging(l => l.AddProvider(logs)));
        var browser = await SignedInAsync(host, Admin);

        var invite = InviteToken(await (await browser.SubmitAsync($"{InvitesPage}?handler=Create")).Content.ReadAsStringAsync());
        var reset = ResetToken(await (await ActAsync(browser, "Reset", samId)).Content.ReadAsStringAsync());
        await host.CreateClient().PostAsJsonAsync(
            "/auth/register", new RegisterUserRequest("invited@example.com", Password, null, InviteToken: invite));
        await host.CreateClient().PostAsJsonAsync("/auth/password/reset", new ResetPasswordRequest(reset, NewPassword));

        Assert.Contains(logs.Lines, l => l.StartsWith("AdminAction:") && l.Contains("create-invite") && l.Contains("on invite"));
        Assert.Contains(logs.Lines, l => l.StartsWith("AdminAction:") && l.Contains("issue-password-reset"));
        Assert.DoesNotContain(logs.Lines, l => l.Contains(invite));
        Assert.DoesNotContain(logs.Lines, l => l.Contains(reset));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private WebApplicationFactory<Program> PortalHost(params (string Key, string? Value)[] overrides) =>
        fixture.CreateHost(
        [
            ("Web:Enabled", "true"),
            ("Web:PublicBaseUrl", PublicBaseUrl),
            ("Auth:LoginRateLimitPerMinute", "200"),
            .. overrides,
        ]);

    private static async Task<PortalBrowser> SignedInAsync(WebApplicationFactory<Program> host, string email)
    {
        var browser = PortalBrowser.Open(host);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.SignInAsync(email, Password)).StatusCode);
        return browser;
    }

    /// <summary>Clicks one of the per-row action buttons on the users page, as the form would post it.</summary>
    private static Task<HttpResponseMessage> ActAsync(
        PortalBrowser browser, string handler, int userId, params (string Name, string? Value)[] extra) =>
        browser.SubmitAsync($"{UsersPage}?handler={handler}", [("userId", userId.ToString()), .. extra]);

    private static string ResetToken(string html) => LinkToken(ResetLink(), html);

    private static string InviteToken(string html) => LinkToken(InviteLink(), html);

    private static string LinkToken(Regex link, string html)
    {
        var match = link.Match(html);
        Assert.True(match.Success, "No one-time link on the page.");
        return match.Groups[1].Value;
    }

    [GeneratedRegex("value=\"https://ledger\\.example\\.test/portal/reset#token=([A-Za-z0-9_-]+)\"")]
    private static partial Regex ResetLink();

    [GeneratedRegex("value=\"https://ledger\\.example\\.test/portal/register#invite=([A-Za-z0-9_-]+)\"")]
    private static partial Regex InviteLink();

    private async Task<int> SeedUserAsync(string email, bool isAdmin = false, bool locked = false, bool disabled = false)
    {
        using var db = fixture.CreateDbContext();
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = new PasswordService().Hash(Password),
            IsAdmin = isAdmin,
            FailedLoginCount = locked ? 5 : 0,
            LockoutEndUtc = locked ? DateTime.UtcNow.AddMinutes(15) : null,
            DisabledAtUtc = disabled ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task SeedProjectAsync(int userId)
    {
        using var db = fixture.CreateDbContext();
        db.Projects.Add(new Project
        {
            UserId = userId,
            SyncId = Guid.NewGuid(),
            Version = 1,
            Name = "Client work",
            OriginDeviceId = "seed-device",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<User> UserAsync(int userId)
    {
        using var db = fixture.CreateDbContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    private static Task<HttpResponseMessage> DeviceLoginAsync(
        WebApplicationFactory<Program> host, string email, string password) =>
        host.CreateClient().PostAsJsonAsync("/auth/login", new LoginRequest(email, password, "device-a", null));

    private static async Task<string> DeviceTokenAsync(WebApplicationFactory<Program> host, string email)
    {
        var response = await DeviceLoginAsync(host, email, Password);
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
