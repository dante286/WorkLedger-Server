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
/// The link-driven onboarding pages (S11 items 19–21): <c>/portal/setup</c>, <c>/portal/register</c> and
/// <c>/portal/reset</c>. The rule they share is asserted directly — <b>loading a page spends nothing</b>,
/// whatever the URL carries — alongside each page's redemption rules, which are S10's.
/// <para>
/// Invites and resets are minted straight into the database with <see cref="SingleUseToken"/>, the way
/// the services mint them, so each test can hold the raw token without driving the operator pages that
/// <c>PortalAdminPagesTests</c> already cover.
/// </para>
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public class PortalOnboardingTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string Email = "new.person@example.com";
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a brand new passphrase";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- setup: one state, one step, always the secret --------------------------------------

    [Fact]
    public async Task Setup_OnAnEmptyServer_CreatesTheAdministratorAndSignsThemIn()
    {
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);

        var page = await (await browser.GetAsync("/portal/setup")).Content.ReadAsStringAsync();
        var response = await CreateFirstAccountAsync(browser, TestConfig.BootstrapSecret);

        Assert.Contains("Create administrator account", page);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/portal?welcome=true", response.Headers.Location!.OriginalString);
        var welcome = await (await browser.GetAsync("/portal?welcome=true")).Content.ReadAsStringAsync();
        Assert.Contains("Your account is ready", welcome);
        Assert.Contains("https://ledger.example.test", welcome);

        using var db = fixture.CreateDbContext();
        var user = await db.Users.SingleAsync();
        Assert.True(user.IsAdmin);
        Assert.Empty(db.Devices);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/portal/setup")).StatusCode);
    }

    /// <summary>
    /// The flags decide how other people get accounts; they must not leave a fresh server with no way to
    /// create its first one.
    /// </summary>
    [Fact]
    public async Task Setup_OnAnEmptyServer_WorksWithRegistrationAndTheSignupSecretSwitchedOff()
    {
        using var host = PortalHost(("Auth:RegistrationEnabled", "false"), ("Auth:SignupSecretEnabled", "false"));

        var response = await CreateFirstAccountAsync(PortalBrowser.Open(host), TestConfig.BootstrapSecret);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var db = fixture.CreateDbContext();
        Assert.True((await db.Users.SingleAsync()).IsAdmin);
    }

    [Fact]
    public async Task Setup_OnAnEmptyServer_RefusesAWrongSecretOrMismatchedPasswords_AndCreatesNothing()
    {
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);

        var wrongSecret = await CreateFirstAccountAsync(browser, "not-the-secret");
        var mismatch = await browser.SubmitAsync(
            "/portal/setup",
            ("Secret", TestConfig.BootstrapSecret), ("Email", Email),
            ("Password", Password), ("ConfirmPassword", Password + "!"));

        Assert.Contains("isn&#x27;t this server&#x27;s setup secret", await wrongSecret.Content.ReadAsStringAsync());
        Assert.Contains("don&#x27;t match", await mismatch.Content.ReadAsStringAsync());
        using var db = fixture.CreateDbContext();
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task Setup_WithTheLegacyRow_ClaimsItInPlace_AndMakesItTheAdministrator()
    {
        int legacyId;
        using (var db = fixture.CreateDbContext())
        {
            var legacy = new User { DisplayName = "Old account", CreatedAt = DateTime.UtcNow };
            db.Users.Add(legacy);
            await db.SaveChangesAsync();
            legacyId = legacy.Id;
        }

        using var host = PortalHost(("Auth:SignupSecretEnabled", "false"));
        var browser = PortalBrowser.Open(host);

        var page = await (await browser.GetAsync("/portal/setup")).Content.ReadAsStringAsync();
        var wrong = await CreateFirstAccountAsync(browser, "not-the-secret");
        var claimed = await CreateFirstAccountAsync(browser, TestConfig.BootstrapSecret);

        Assert.Contains("Claim this server", page);
        Assert.Contains("setup secret", await wrong.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, claimed.StatusCode);

        using var after = fixture.CreateDbContext();
        var user = await after.Users.SingleAsync();
        Assert.Equal(legacyId, user.Id);
        Assert.Equal(Email, user.Email);
        Assert.NotNull(user.PasswordHash);
        Assert.True(user.IsAdmin);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal/admin")).StatusCode);
    }

    [Fact]
    public async Task Setup_WithUsersButNoAdmin_LetsOneSignInAndPromoteThemselves()
    {
        var samId = await SeedUserAsync("sam@example.com");
        await SeedUserAsync("alex@example.com");
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);

        var page = await (await browser.GetAsync("/portal/setup")).Content.ReadAsStringAsync();
        var wrongSecret = await PromoteAsync(browser, "sam@example.com", Password, "not-the-secret");
        var wrongPassword = await PromoteAsync(browser, "sam@example.com", "wrong password here", TestConfig.BootstrapSecret);
        var promoted = await PromoteAsync(browser, "sam@example.com", Password, TestConfig.BootstrapSecret);

        Assert.Contains("Become administrator", page);
        Assert.Contains("setup secret", await wrongSecret.Content.ReadAsStringAsync());
        Assert.Contains("Invalid email or password.", await wrongPassword.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, promoted.StatusCode);
        Assert.True((await UserAsync(samId)).IsAdmin);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/portal/setup")).StatusCode);
    }

    /// <summary>A wrong secret is refused before the password is checked, so it costs the account nothing.</summary>
    [Fact]
    public async Task Setup_AWrongSecretSpendsNothingFromTheAccountsSignInBudget()
    {
        var samId = await SeedUserAsync("sam@example.com");
        using var host = PortalHost();

        await PromoteAsync(PortalBrowser.Open(host), "sam@example.com", "wrong password here", "not-the-secret");

        Assert.Equal(0, (await UserAsync(samId)).FailedLoginCount);
    }

    [Fact]
    public async Task Setup_OnceAnAdminExists_IsNotAPage()
    {
        await SeedUserAsync("admin@example.com", isAdmin: true);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);
        var token = await browser.FormTokenAsync("/portal/login");

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/portal/setup")).StatusCode);
        var post = await browser.PostAsync(
            "/portal/setup", token, ("Secret", TestConfig.BootstrapSecret), ("Email", "admin@example.com"), ("Password", Password));
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.DoesNotContain("/portal/setup", await (await browser.GetAsync("/portal/login")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheSignInPage_PointsAFirstRunOperatorAtSetup()
    {
        using var host = PortalHost();

        var html = await (await PortalBrowser.Open(host).GetAsync("/portal/login")).Content.ReadAsStringAsync();

        Assert.Contains("href=\"/portal/setup\"", html);
    }

    // ---- register ---------------------------------------------------------------------------

    /// <summary>
    /// A link preview, or anything else that GETs the page, spends nothing — and a token smuggled into the
    /// query string (rather than the fragment, which never arrives) is not consulted either.
    /// </summary>
    [Fact]
    public async Task LoadingTheRegisterOrResetPage_WritesNothing_WhateverTheUrlCarries()
    {
        var samId = await SeedUserAsync("sam@example.com");
        var invite = await SeedInviteAsync();
        var reset = await SeedResetAsync(samId);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);

        var register = await browser.GetAsync($"/portal/register?invite={invite}&InviteToken={invite}");
        var resetPage = await browser.GetAsync($"/portal/reset?token={reset}&Token={reset}");

        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resetPage.StatusCode);
        using var db = fixture.CreateDbContext();
        Assert.Null((await db.Invites.SingleAsync()).RedeemedAtUtc);
        Assert.Null((await db.PasswordResets.SingleAsync()).RedeemedAtUtc);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task TheRegisterAndResetPages_TakeTheirTokenFromTheFragment_OrAPastedCode()
    {
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);

        var register = await (await browser.GetAsync("/portal/register")).Content.ReadAsStringAsync();
        var reset = await (await browser.GetAsync("/portal/reset")).Content.ReadAsStringAsync();

        Assert.Contains("name=\"InviteToken\" type=\"text\"", register);
        Assert.Contains("data-fragment=\"invite\"", register);
        Assert.Contains("name=\"Token\" type=\"text\"", reset);
        Assert.Contains("data-fragment=\"token\"", reset);
    }

    [Fact]
    public async Task Register_WithAnInvite_CreatesTheAccountOnce_SignsItIn_AndShowsTheServerAddress()
    {
        // An admin already exists, as it would for anyone to have issued an invite — otherwise the
        // registration below would be the first account and, by S10's rule, an admin itself.
        await SeedUserAsync("admin@example.com", isAdmin: true);
        var invite = await SeedInviteAsync();
        using var host = PortalHost(("Auth:SignupSecretEnabled", "false"));
        var browser = PortalBrowser.Open(host);

        var response = await RegisterAsync(browser, invite, Email);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/portal?welcome=true", response.Headers.Location!.OriginalString);
        var home = await (await browser.GetAsync("/portal?welcome=true")).Content.ReadAsStringAsync();
        Assert.Contains(Email, home);
        Assert.Contains("value=\"https://ledger.example.test\"", home);

        using (var db = fixture.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(u => u.Email == Email);
            var spent = await db.Invites.SingleAsync();
            Assert.False(user.IsAdmin);
            Assert.Equal(user.Id, spent.RedeemedByUserId);
        }

        var again = await RegisterAsync(PortalBrowser.Open(host), invite, "someone.else@example.com");
        Assert.Contains("That invite has already been used.", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Register_FailingOnADuplicateEmail_LeavesTheInviteUnspent()
    {
        await SeedUserAsync(Email);
        var invite = await SeedInviteAsync();
        using var host = PortalHost();

        var response = await RegisterAsync(PortalBrowser.Open(host), invite, Email);

        Assert.Contains("An account with that email already exists.", await response.Content.ReadAsStringAsync());
        using var db = fixture.CreateDbContext();
        Assert.Null((await db.Invites.SingleAsync()).RedeemedAtUtc);
    }

    [Fact]
    public async Task Register_WithAnInviteForSomeoneElse_OrAnExpiredOne_ShowsTheServersOwnSentence()
    {
        var boundElsewhere = await SeedInviteAsync(boundTo: "someone.else@example.com");
        var expired = await SeedInviteAsync(expiresInDays: -1);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);

        var wrongAddress = await RegisterAsync(browser, boundElsewhere, Email);
        var tooLate = await RegisterAsync(browser, expired, Email);
        var unknown = await RegisterAsync(browser, "not-a-real-invite", Email);

        Assert.Contains("That invite was issued for a different email address.", await wrongAddress.Content.ReadAsStringAsync());
        Assert.Contains("That invite has expired. Ask for a new one.", await tooLate.Content.ReadAsStringAsync());
        Assert.Contains("isn&#x27;t valid", await unknown.Content.ReadAsStringAsync());
        using var db = fixture.CreateDbContext();
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task Register_AcceptsTheSignupSecretOnlyWhileItIsEnabled()
    {
        using (var enabled = PortalHost(("Auth:SignupSecretEnabled", "true")))
        {
            var browser = PortalBrowser.Open(enabled);
            Assert.Contains("signup secret", await (await browser.GetAsync("/portal/register")).Content.ReadAsStringAsync());
            var response = await RegisterAsync(browser, invite: null, Email, secret: TestConfig.BootstrapSecret);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        await fixture.ResetAsync();
        using var disabled = PortalHost(("Auth:SignupSecretEnabled", "false"));
        var refused = PortalBrowser.Open(disabled);
        Assert.DoesNotContain("signup secret", await (await refused.GetAsync("/portal/register")).Content.ReadAsStringAsync());
        var denied = await RegisterAsync(refused, invite: null, Email, secret: TestConfig.BootstrapSecret);
        Assert.Contains("invite code isn&#x27;t valid", await denied.Content.ReadAsStringAsync());
        using var db = fixture.CreateDbContext();
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task Register_WhenRegistrationIsClosed_SaysSo_AndCreatesNothing()
    {
        var invite = await SeedInviteAsync();
        using var host = PortalHost(("Auth:RegistrationEnabled", "false"));
        var browser = PortalBrowser.Open(host);

        var page = await (await browser.GetAsync("/portal/register")).Content.ReadAsStringAsync();
        // The closed page renders no form, so a forged post borrows its token from the sign-in page.
        var token = await browser.FormTokenAsync("/portal/login");
        await browser.PostAsync(
            "/portal/register", token,
            ("InviteToken", invite), ("Email", Email), ("Password", Password), ("ConfirmPassword", Password));

        Assert.Contains("Registration is closed", page);
        Assert.DoesNotContain("<form method=\"post\" action=\"/portal/register\"", page);
        using var db = fixture.CreateDbContext();
        Assert.Empty(db.Users);
        Assert.Null((await db.Invites.SingleAsync()).RedeemedAtUtc);
    }

    // ---- reset ------------------------------------------------------------------------------

    [Fact]
    public async Task Reset_ChangesThePassword_SignsEveryDeviceOut_ClearsTheLockout_AndSignsNobodyIn()
    {
        var samId = await SeedUserAsync("sam@example.com");
        using var host = PortalHost();
        var device = Bearer(host, await DeviceTokenAsync(host, "sam@example.com", Password));
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/auth/devices")).StatusCode);
        await LockAsync(samId);
        var reset = await SeedResetAsync(samId);
        var browser = PortalBrowser.Open(host);

        var response = await RedeemAsync(browser, reset, NewPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            c => c.StartsWith("__Host-WorkLedger=", StringComparison.Ordinal));
        var done = await (await browser.GetAsync(response.Headers.Location!.OriginalString)).Content.ReadAsStringAsync();
        Assert.Contains("Every device has been signed out", done);
        Assert.Contains("href=\"/portal/login\"", done);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/portal")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/auth/devices")).StatusCode);
        var user = await UserAsync(samId);
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockoutEndUtc);
        Assert.Equal(HttpStatusCode.OK, (await DeviceLoginAsync(host, "sam@example.com", NewPassword)).StatusCode);
    }

    [Fact]
    public async Task Reset_ASpentTokenAndAnUnknownOne_ShowTheSameMessage()
    {
        var samId = await SeedUserAsync("sam@example.com");
        var reset = await SeedResetAsync(samId);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);
        await RedeemAsync(browser, reset, NewPassword);

        var spent = await RedeemAsync(browser, reset, "yet another passphrase");
        var unknown = await RedeemAsync(browser, "not-a-real-token", "yet another passphrase");

        // Up to the dash, which Razor encodes as an entity.
        var message = PasswordResetService.InvalidTokenMessage[..PasswordResetService.InvalidTokenMessage.IndexOf('—')];
        Assert.Contains(message, await spent.Content.ReadAsStringAsync());
        Assert.Contains(message, await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reset_WithATooShortPassword_LeavesTheTokenSpendable_AndSaysSo()
    {
        var samId = await SeedUserAsync("sam@example.com");
        var reset = await SeedResetAsync(samId);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);

        var tooShort = await RedeemAsync(browser, reset, "short");
        var html = await tooShort.Content.ReadAsStringAsync();

        Assert.Contains("still works", html);
        Assert.Equal(HttpStatusCode.Redirect, (await RedeemAsync(browser, reset, NewPassword)).StatusCode);
    }

    // ---- shared page rules ------------------------------------------------------------------

    [Theory]
    [InlineData("/portal/setup")]
    [InlineData("/portal/register")]
    [InlineData("/portal/reset")]
    public async Task APostWithoutTheAntiforgeryToken_Is400_AndWritesNothing(string page)
    {
        var invite = await SeedInviteAsync();
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);
        await browser.GetAsync(page);

        var response = await browser.PostAsync(
            page, null,
            ("Secret", TestConfig.BootstrapSecret), ("InviteToken", invite), ("Email", Email),
            ("Password", Password), ("ConfirmPassword", Password));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var db = fixture.CreateDbContext();
        Assert.Empty(db.Users);
    }

    /// <summary>They take secrets and tokens from anyone, so they share the sign-in rate limit — but a GET is free.</summary>
    [Theory]
    [InlineData("/portal/setup")]
    [InlineData("/portal/register")]
    [InlineData("/portal/reset")]
    public async Task EachOnboardingPage_IsRateLimitedOnPost(string page)
    {
        using var host = fixture.CreateHost(
            ("Web:Enabled", "true"),
            ("Web:PublicBaseUrl", "https://ledger.example.test"),
            ("Auth:LoginRateLimitPerMinute", "1"),
            ("Auth:LoginRateLimitWindowSeconds", "60"));
        var browser = PortalBrowser.Open(host);
        var token = await browser.FormTokenAsync(page);

        await browser.PostAsync(page, token, ("Email", Email));
        var throttled = await browser.PostAsync(page, token, ("Email", Email));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(page)).StatusCode);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private WebApplicationFactory<Program> PortalHost(params (string Key, string? Value)[] overrides) =>
        fixture.CreateHost(
        [
            ("Web:Enabled", "true"),
            ("Web:PublicBaseUrl", "https://ledger.example.test"),
            ("Auth:LoginRateLimitPerMinute", "200"),
            .. overrides,
        ]);

    private static Task<HttpResponseMessage> CreateFirstAccountAsync(PortalBrowser browser, string secret) =>
        browser.SubmitAsync(
            "/portal/setup",
            ("Secret", secret), ("Email", Email), ("DisplayName", "First"),
            ("Password", Password), ("ConfirmPassword", Password));

    private static Task<HttpResponseMessage> PromoteAsync(PortalBrowser browser, string email, string password, string secret) =>
        browser.SubmitAsync("/portal/setup", ("Secret", secret), ("Email", email), ("Password", password));

    private static Task<HttpResponseMessage> RegisterAsync(
        PortalBrowser browser, string? invite, string email, string? secret = null) =>
        browser.SubmitAsync(
            "/portal/register",
            ("InviteToken", invite), ("Secret", secret), ("Email", email), ("DisplayName", "New Person"),
            ("Password", Password), ("ConfirmPassword", Password));

    private static Task<HttpResponseMessage> RedeemAsync(PortalBrowser browser, string token, string password) =>
        browser.SubmitAsync("/portal/reset", ("Token", token), ("Password", password), ("ConfirmPassword", password));

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

    private async Task<string> SeedInviteAsync(string? boundTo = null, int expiresInDays = 7)
    {
        var token = SingleUseToken.Generate();
        using var db = fixture.CreateDbContext();
        db.Invites.Add(new Invite
        {
            TokenHash = SingleUseToken.Hash(token),
            NormalizedEmail = boundTo?.ToUpperInvariant(),
            CreatedAt = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(expiresInDays),
        });
        await db.SaveChangesAsync();
        return token;
    }

    private async Task<string> SeedResetAsync(int userId)
    {
        var token = SingleUseToken.Generate();
        using var db = fixture.CreateDbContext();
        db.PasswordResets.Add(new PasswordReset
        {
            TokenHash = SingleUseToken.Hash(token),
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync();
        return token;
    }

    private async Task LockAsync(int userId)
    {
        using var db = fixture.CreateDbContext();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.FailedLoginCount = 5;
        user.LockoutEndUtc = DateTime.UtcNow.AddMinutes(15);
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

    private static async Task<string> DeviceTokenAsync(WebApplicationFactory<Program> host, string email, string password)
    {
        var response = await DeviceLoginAsync(host, email, password);
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
