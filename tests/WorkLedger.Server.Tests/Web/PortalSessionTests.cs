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
/// Portal sign-in and the session it creates (S11 items 6, 7 and 10): the shared credential check and
/// lockout, the cookie itself, the wall between the cookie and bearer schemes, antiforgery, and
/// revocation.
/// <para>
/// <b>Every host here runs <c>Auth:StampCacheSeconds</c> at its default</b>, and the revocation tests warm
/// the cache with a successful request before revoking. A portal session ending "eventually" is not the
/// claim; ending on the very next request is, and that only holds while every revoking path evicts the
/// per-user key. Switching the cache off would let these pass with the eviction missing.
/// </para>
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public class PortalSessionTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string Email = "sam@example.com";
    private const string Password = "correct horse battery staple";
    private const string WrongPassword = "correct horse battery stapler";
    private const string InvalidMessage = "Invalid email or password.";
    private const string LockedMessage = "This account is temporarily locked";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- sign-in ----------------------------------------------------------------------------

    [Fact]
    public async Task SignIn_SetsAHostPrefixedSessionCookie_AndCreatesNoDevice()
    {
        var userId = await SeedUserAsync(Email);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);

        var response = await browser.SignInAsync(Email, Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/portal", response.Headers.Location!.OriginalString);

        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith("__Host-WorkLedger=", StringComparison.Ordinal))
            .ToLowerInvariant();
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("domain=", cookie);
        // Session-only: no "remember me", so the browser drops it on close.
        Assert.DoesNotContain("expires=", cookie);

        var home = await browser.GetAsync("/portal");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains(Email, await home.Content.ReadAsStringAsync());

        using var db = fixture.CreateDbContext();
        Assert.Empty(db.Devices);
        Assert.NotNull((await db.Users.SingleAsync(u => u.Id == userId)).LastLoginUtc);
    }

    [Fact]
    public async Task SignIn_WrongPasswordUnknownEmailAndSuspendedAccount_AllShowTheSameMessage()
    {
        await SeedUserAsync(Email);
        await SeedUserAsync("suspended@example.com", disabled: true);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);

        var responses = new[]
        {
            await browser.SignInAsync(Email, WrongPassword),
            await browser.SignInAsync("nobody@example.com", Password),
            await browser.SignInAsync("suspended@example.com", Password),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(InvalidMessage, await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain(
                response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
                c => c.StartsWith("__Host-WorkLedger=", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// One account, one password, one budget: failures at the portal and at <c>/auth/login</c> count
    /// against the same counter, and the device attempt that crosses the threshold is the one locked out.
    /// </summary>
    [Fact]
    public async Task PortalFailures_CountTowardsTheDeviceLoginLockout()
    {
        await SeedUserAsync(Email);
        using var host = PortalHost(("Auth:MaxFailedLogins", "3"));
        var browser = PortalBrowser.Open(host);

        await browser.SignInAsync(Email, WrongPassword);
        await browser.SignInAsync(Email, WrongPassword);
        var device = await DeviceLoginAsync(host, Email, WrongPassword);

        Assert.Equal(HttpStatusCode.Locked, device.StatusCode);
        Assert.Equal(HttpStatusCode.Locked, (await DeviceLoginAsync(host, Email, Password)).StatusCode);
    }

    [Fact]
    public async Task DeviceLoginFailures_CountTowardsThePortalLockout()
    {
        await SeedUserAsync(Email);
        using var host = PortalHost(("Auth:MaxFailedLogins", "3"));
        var browser = PortalBrowser.Open(host);

        await DeviceLoginAsync(host, Email, WrongPassword);
        await DeviceLoginAsync(host, Email, WrongPassword);
        var crossing = await browser.SignInAsync(Email, WrongPassword);
        var correct = await browser.SignInAsync(Email, Password);

        // "About N minutes", never an attempts-remaining count.
        var crossingHtml = await crossing.Content.ReadAsStringAsync();
        Assert.Contains(LockedMessage, crossingHtml);
        Assert.Contains("try again in about 15 minutes", crossingHtml);
        Assert.Contains(LockedMessage, await correct.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SignIn_WhileLocked_WritesNothing()
    {
        var userId = await SeedUserAsync(Email);
        using var host = PortalHost(("Auth:MaxFailedLogins", "2"));
        var browser = PortalBrowser.Open(host);
        await browser.SignInAsync(Email, WrongPassword);
        await browser.SignInAsync(Email, WrongPassword);
        var before = await CountersAsync(userId);
        Assert.NotNull(before.LockoutEndUtc);

        var response = await browser.SignInAsync(Email, Password);

        Assert.Contains(LockedMessage, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await CountersAsync(userId));
    }

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example/phish")]
    [InlineData("/\\evil.example/phish")]
    public async Task SignIn_IgnoresAnOffSiteReturnUrl(string returnUrl)
    {
        await SeedUserAsync(Email);
        using var host = PortalHost();

        var response = await PortalBrowser.Open(host).SignInAsync(Email, Password, returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/portal", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task SignIn_HonoursALocalReturnUrl()
    {
        await SeedUserAsync(Email, isAdmin: true);
        using var host = PortalHost();

        var response = await PortalBrowser.Open(host).SignInAsync(Email, Password, "/portal/admin");

        Assert.Equal("/portal/admin", response.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// Host-relative, not <c>http://localhost/portal/login?...</c>: behind a TLS proxy the process sees
    /// http and an untrusted Host header, so an absolute redirect would be wrong or attacker-chosen.
    /// </summary>
    [Fact]
    public async Task AnAnonymousPortalRequest_RedirectsToSignIn_HostRelative()
    {
        using var host = PortalHost();

        var response = await PortalBrowser.Open(host).GetAsync("/portal");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/portal/login?ReturnUrl=", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ThePortalSignInForm_SharesTheLoginRateLimitBucket_ButRenderingItIsFree()
    {
        await SeedUserAsync(Email);
        using var host = fixture.CreateHost(
            ("Web:Enabled", "true"),
            ("Web:PublicBaseUrl", "https://ledger.example.test"),
            ("Auth:LoginRateLimitPerMinute", "2"),
            ("Auth:LoginRateLimitWindowSeconds", "60"));
        var browser = PortalBrowser.Open(host);
        var token = await browser.FormTokenAsync("/portal/login");

        await DeviceLoginAsync(host, Email, WrongPassword);
        await DeviceLoginAsync(host, Email, WrongPassword);
        var throttled = await browser.PostAsync("/portal/login", token, ("Email", Email), ("Password", Password));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);
        Assert.Contains("Too many sign-in attempts", await throttled.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal/login")).StatusCode);
    }

    // ---- the wall between the schemes -------------------------------------------------------

    /// <summary>
    /// The whole reason the schemes are kept apart: a cookie the browser attaches automatically must never
    /// authenticate the JSON API, where nothing checks antiforgery. The signed-in user is an admin, so a
    /// leak would show up as a 200 on <c>/admin/users</c>.
    /// </summary>
    [Fact]
    public async Task APortalSession_NeverAuthenticatesTheJsonApi()
    {
        await SeedUserAsync(Email, isAdmin: true);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);
        await browser.SignInAsync(Email, Password);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal/admin")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/whoami")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await browser.Client.PostAsJsonAsync("/sync/push", new { Changes = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task ABearerToken_NeverAuthenticatesAPortalPage()
    {
        await SeedUserAsync(Email, isAdmin: true);
        using var host = PortalHost();
        var token = await DeviceTokenAsync(host, Email);
        var device = PortalBrowser.Open(host);
        device.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        foreach (var page in new[] { "/portal", "/portal/admin" })
        {
            var response = await device.GetAsync(page);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/portal/login", response.Headers.Location!.OriginalString);
        }

        // And the sign-in page does not treat the token as a session either.
        var login = await device.GetAsync("/portal/login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.DoesNotContain("Sign out", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithThePortalOn_AnUnauthenticatedJsonRequest_IsStill401_NotARedirect()
    {
        using var host = PortalHost();

        var response = await PortalBrowser.Open(host).GetAsync("/auth/devices");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task ARegularUser_IsRefusedTheAdminPages()
    {
        await SeedUserAsync(Email);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);
        await browser.SignInAsync(Email, Password);

        var response = await browser.GetAsync("/portal/admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/portal/denied", response.Headers.Location!.OriginalString);
    }

    // ---- antiforgery ------------------------------------------------------------------------

    [Fact]
    public async Task ASignInPostWithoutTheAntiforgeryToken_Is400_AndWritesNothing()
    {
        var userId = await SeedUserAsync(Email);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);
        await browser.GetAsync("/portal/login");

        var response = await browser.PostAsync("/portal/login", null, ("Email", Email), ("Password", WrongPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(new Counters(0, null), await CountersAsync(userId));
    }

    [Fact]
    public async Task ASignOutPostWithoutTheAntiforgeryToken_Is400_AndTheSessionSurvives()
    {
        await SeedUserAsync(Email);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);
        await browser.SignInAsync(Email, Password);

        var response = await browser.PostAsync("/portal/logout", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal")).StatusCode);
    }

    // ---- revocation, at the default stamp cache ---------------------------------------------

    [Fact]
    public async Task SignOutEverywhereFromADevice_EndsThePortalSessionOnTheNextRequest()
    {
        await SeedUserAsync(Email);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);
        await browser.SignInAsync(Email, Password);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal")).StatusCode);

        var device = Bearer(host, await DeviceTokenAsync(host, Email));
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsync("/auth/logout-all", null)).StatusCode);

        var response = await browser.GetAsync("/portal");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/portal/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task SuspendingTheUser_EndsThePortalSessionOnTheNextRequest()
    {
        await SeedUserAsync("admin@example.com", isAdmin: true);
        var userId = await SeedUserAsync(Email);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);
        await browser.SignInAsync(Email, Password);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal")).StatusCode);

        var admin = Bearer(host, await DeviceTokenAsync(host, "admin@example.com"));
        Assert.Equal(
            HttpStatusCode.NoContent, (await admin.PostAsync($"/admin/users/{userId}/disable", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/portal")).StatusCode);
    }

    [Fact]
    public async Task ADemotedAdmin_IsRefusedTheAdminPagesOnTheNextRequest_ButStaysSignedIn()
    {
        await SeedUserAsync("first@example.com", isAdmin: true);
        var secondId = await SeedUserAsync(Email, isAdmin: true);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);
        await browser.SignInAsync(Email, Password);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal/admin")).StatusCode);

        var first = Bearer(host, await DeviceTokenAsync(host, "first@example.com"));
        Assert.Equal(
            HttpStatusCode.NoContent, (await first.DeleteAsync($"/admin/users/{secondId}/admin")).StatusCode);

        var refused = await browser.GetAsync("/portal/admin");
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.StartsWith("/portal/denied", refused.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal")).StatusCode);
    }

    [Fact]
    public async Task DeletingTheAccount_EndsThePortalSessionOnTheNextRequest()
    {
        await SeedUserAsync(Email);
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);
        await browser.SignInAsync(Email, Password);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/portal")).StatusCode);

        var device = Bearer(host, await DeviceTokenAsync(host, Email));
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest(Password, Email)),
        };
        Assert.Equal(HttpStatusCode.NoContent, (await device.SendAsync(delete)).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/portal")).StatusCode);
    }

    /// <summary>Signing a browser out must not sign out every desktop install: portal logout bumps nothing.</summary>
    [Fact]
    public async Task PortalSignOut_EndsTheSession_AndLeavesDeviceTokensWorking()
    {
        await SeedUserAsync(Email);
        using var host = PortalHost();
        var device = Bearer(host, await DeviceTokenAsync(host, Email));
        var browser = PortalBrowser.Open(host);
        await browser.SignInAsync(Email, Password);

        var signOut = await browser.SignOutAsync();

        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);
        Assert.Equal("/portal/login", signOut.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/portal")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/auth/devices")).StatusCode);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private sealed record Counters(int FailedLoginCount, DateTime? LockoutEndUtc);

    private WebApplicationFactory<Program> PortalHost(params (string Key, string? Value)[] overrides) =>
        fixture.CreateHost(
        [
            ("Web:Enabled", "true"),
            ("Web:PublicBaseUrl", "https://ledger.example.test"),
            ("Auth:LoginRateLimitPerMinute", "200"),
            .. overrides,
        ]);

    private async Task<int> SeedUserAsync(string email, bool isAdmin = false, bool disabled = false)
    {
        using var db = fixture.CreateDbContext();
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = new PasswordService().Hash(Password),
            IsAdmin = isAdmin,
            DisabledAtUtc = disabled ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Counters> CountersAsync(int userId)
    {
        using var db = fixture.CreateDbContext();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        return new Counters(user.FailedLoginCount, user.LockoutEndUtc);
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
