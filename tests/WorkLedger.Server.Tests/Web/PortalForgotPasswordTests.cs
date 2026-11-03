using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data.Entities;
using WorkLedger.Server.Email;
using WorkLedger.Server.Tests.Auth;
using WorkLedger.Server.Tests.Email;

namespace WorkLedger.Server.Tests.Web;

/// <summary>
/// S12 group C: <c>/portal/forgot</c>. The page answers identically for every address and only queues; the
/// delivery worker decides who gets a message (an active account with a password), applies the per-user
/// cooldown, and issues a short-lived self-service reset that kills any outstanding one.
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public partial class PortalForgotPasswordTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string Active = "active@example.com";
    private const string Suspended = "suspended@example.com";
    private const string NoPassword = "no-password@example.com";
    private const string Unknown = "nobody@example.com";
    private const string Admin = "admin@example.com";
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a brand new passphrase";
    private const string ForgotPage = "/portal/forgot";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- email off ----------------------------------------------------------------------------

    [Fact]
    public async Task WithEmailOff_ThePageDoesNotExist_AndSignInDoesNotLinkToIt()
    {
        using var host = Host(emailOn: false, out _);
        var browser = PortalBrowser.Open(host);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(ForgotPage)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await browser.PostAsync(ForgotPage, await browser.FormTokenAsync("/portal/login"), ("Address", Active))).StatusCode);
        Assert.DoesNotContain(ForgotPage, await (await browser.GetAsync("/portal/login")).Content.ReadAsStringAsync());
    }

    // ---- the uniform answer -------------------------------------------------------------------

    /// <summary>
    /// The heart of it: four kinds of address, one answer, byte for byte apart from the antiforgery token —
    /// and only the active account gets a message. The worker reads the queue in order, so once the active
    /// one's message arrives, the three before it have been decided too.
    /// </summary>
    [Fact]
    public async Task EveryAddress_GetsTheSameAnswer_AndOnlyAnActiveAccountGetsMail()
    {
        var activeId = await SeedUserAsync(Active);
        await SeedUserAsync(Suspended, disabled: true);
        await SeedUserAsync(NoPassword, withPassword: false);
        using var host = Host(emailOn: true, out var mail);
        var browser = PortalBrowser.Open(host);

        var bodies = new List<string>();
        foreach (var address in new[] { Unknown, Suspended, NoPassword, Active })
        {
            var response = await browser.SubmitAsync(ForgotPage, ("Address", address));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            bodies.Add(WithoutAntiforgery(await response.Content.ReadAsStringAsync()));
        }

        var sent = Assert.Single(await mail.WaitForAsync());
        Assert.Equal(EmailKind.SelfServicePasswordReset, sent.Kind);
        Assert.Equal(Active, sent.To);
        Assert.Equal(activeId, sent.UserId);
        Assert.All(bodies, body => Assert.Equal(bodies[0], body));
        Assert.Contains("If an account exists for that address", bodies[0]);
        Assert.All(new[] { Unknown, Suspended, NoPassword, Active }, address =>
            Assert.DoesNotContain(address, bodies[0], StringComparison.OrdinalIgnoreCase));
        await mail.AssertCountStaysAsync(1);
    }

    [Fact]
    public async Task TheEmailedLink_Redeems_AndLivesForTheSelfServiceExpiry()
    {
        var activeId = await SeedUserAsync(Active);
        using var host = Host(emailOn: true, out var mail, ("Auth:SelfServiceResetExpiryMinutes", "30"));
        var browser = PortalBrowser.Open(host);

        var before = DateTime.UtcNow;
        await browser.SubmitAsync(ForgotPage, ("Address", "  Active@Example.COM "));
        var token = TokenIn(Assert.Single(await mail.WaitForAsync()));

        var reset = await SingleResetAsync(activeId);
        Assert.True(reset.SelfService);
        Assert.Null(reset.CreatedByUserId);
        Assert.InRange(reset.ExpiresAtUtc, before.AddMinutes(30).AddSeconds(-5), DateTime.UtcNow.AddMinutes(30).AddSeconds(5));
        Assert.Equal(HttpStatusCode.NoContent, (await RedeemAsync(host, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DeviceLoginAsync(host, Active, NewPassword)).StatusCode);
    }

    // ---- the cooldown -------------------------------------------------------------------------

    [Fact]
    public async Task ASecondRequestInsideTheCooldown_SendsNothing_AndKeepsTheFirstLinkAlive()
    {
        var activeId = await SeedUserAsync(Active);
        using var host = Host(emailOn: true, out var mail);
        var browser = PortalBrowser.Open(host);

        await browser.SubmitAsync(ForgotPage, ("Address", Active));
        var first = TokenIn(Assert.Single(await mail.WaitForAsync()));
        var again = WithoutAntiforgery(await (await browser.SubmitAsync(ForgotPage, ("Address", Active))).Content.ReadAsStringAsync());

        Assert.Contains("If an account exists for that address", again);
        await mail.AssertCountStaysAsync(1);
        Assert.Equal(1, await ResetCountAsync(activeId));
        Assert.Equal(HttpStatusCode.NoContent, (await RedeemAsync(host, first)).StatusCode);
    }

    [Fact]
    public async Task ACooldownOfZero_SendsEveryTime()
    {
        await SeedUserAsync(Active);
        using var host = Host(emailOn: true, out var mail, ("Email:ResetCooldownMinutes", "0"));
        var browser = PortalBrowser.Open(host);

        await browser.SubmitAsync(ForgotPage, ("Address", Active));
        await browser.SubmitAsync(ForgotPage, ("Address", Active));

        Assert.Equal(2, (await mail.WaitForAsync(2)).Count);
    }

    /// <summary>
    /// Self-service and admin issues meet in one place: a self-service issue kills the outstanding admin
    /// link, and an admin issue is never held back by the self-service cooldown.
    /// </summary>
    [Fact]
    public async Task SelfServiceKillsAnAdminReset_AndTheCooldownNeverHoldsAnAdminBack()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var activeId = await SeedUserAsync(Active);
        using var host = Host(emailOn: true, out var mail);
        var admin = Bearer(host, await DeviceTokenAsync(host, Admin));
        var browser = PortalBrowser.Open(host);

        var adminToken = await AdminResetAsync(admin, activeId);
        await browser.SubmitAsync(ForgotPage, ("Address", Active));
        var selfServiceToken = TokenIn(Assert.Single(await mail.WaitForAsync()));

        Assert.False((await RedeemAsync(host, adminToken)).IsSuccessStatusCode);

        var laterAdminToken = await AdminResetAsync(admin, activeId);
        Assert.False((await RedeemAsync(host, selfServiceToken)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await RedeemAsync(host, laterAdminToken)).StatusCode);
    }

    // ---- what the request does and does not do -----------------------------------------------

    [Fact]
    public async Task GettingThePage_WritesNothing_AndAForgedPost_Is400_AndQueuesNothing()
    {
        var activeId = await SeedUserAsync(Active);
        using var host = Host(emailOn: true, out var mail);
        var browser = PortalBrowser.Open(host);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(ForgotPage)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync(ForgotPage, null, ("Address", Active))).StatusCode);

        await mail.AssertNoneAsync();
        Assert.Equal(0, await ResetCountAsync(activeId));
    }

    [Fact]
    public async Task AMalformedAddress_IsTurnedBack_WithoutQueueing()
    {
        await SeedUserAsync(Active);
        using var host = Host(emailOn: true, out var mail);
        var browser = PortalBrowser.Open(host);

        var html = await (await browser.SubmitAsync(ForgotPage, ("Address", "not-an-address"))).Content.ReadAsStringAsync();

        Assert.Contains("Enter the email address you sign in with", html);
        Assert.DoesNotContain("If an account exists", html);
        await mail.AssertNoneAsync();
    }

    [Fact]
    public async Task TheForm_SharesSignInsRateLimit()
    {
        using var host = Host(emailOn: true, out _, ("Auth:LoginRateLimitPerMinute", "2"));
        var browser = PortalBrowser.Open(host);

        await browser.SignInAsync(Unknown, Password);
        await browser.SignInAsync(Unknown, Password);
        var response = await browser.SubmitAsync(ForgotPage, ("Address", Unknown));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    /// <summary>
    /// The relay is stuck and the queue is full: a request that could not even be queued gets the same page
    /// as one that was. Capacity 1 — the worker holds one request's message, the queue holds the next.
    /// </summary>
    [Fact]
    public async Task AFullQueue_LeavesTheAnswerUnchanged()
    {
        await SeedUserAsync(Active);
        await SeedUserAsync("second@example.com");
        var relay = new TaskCompletionSource();
        using var host = Host(emailOn: true, out var mail, ("Email:QueueCapacity", "1"));
        mail.OnSend = (_, _) => relay.Task;
        var browser = PortalBrowser.Open(host);

        await browser.SubmitAsync(ForgotPage, ("Address", Active));
        await mail.WaitForAsync();
        var queued = WithoutAntiforgery(await (await browser.SubmitAsync(ForgotPage, ("Address", "second@example.com"))).Content.ReadAsStringAsync());
        var dropped = WithoutAntiforgery(await (await browser.SubmitAsync(ForgotPage, ("Address", Unknown))).Content.ReadAsStringAsync());
        relay.SetResult();

        Assert.Equal(queued, dropped);
    }

    /// <summary>Across every outcome, no requested address and no token reaches a log line; ids do.</summary>
    [Fact]
    public async Task NoLogLine_CarriesAnAddressOrAToken()
    {
        var activeId = await SeedUserAsync(Active);
        await SeedUserAsync(Suspended, disabled: true);
        var logs = new CapturedLogs();
        using var baseHost = Host(emailOn: true, out var mail);
        using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureLogging(l => l
            .AddProvider(logs)
            .AddFilter<CapturedLogs>(category: null, LogLevel.Trace)));
        var browser = PortalBrowser.Open(host);

        foreach (var address in new[] { Unknown, Suspended, Active, Active })
        {
            await browser.SubmitAsync(ForgotPage, ("Address", address));
        }

        var token = TokenIn(Assert.Single(await mail.WaitForAsync()));
        await mail.AssertCountStaysAsync(1);

        Assert.Contains(logs.Lines, l => l.Contains($"User {activeId}: self-service-reset-issued"));
        Assert.Contains(logs.Lines, l => l.Contains($"for user {activeId} not issued"));
        foreach (var line in logs.Lines)
        {
            Assert.DoesNotContain(token, line);
            foreach (var address in new[] { Unknown, Suspended, Active })
            {
                Assert.False(line.Contains(address, StringComparison.OrdinalIgnoreCase), "LEAK: " + line);
            }
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private WebApplicationFactory<Program> Host(
        bool emailOn, out RecordingEmailSender mail, params (string Key, string? Value)[] overrides)
    {
        var recorder = new RecordingEmailSender();
        mail = recorder;
        (string, string?)[] email = emailOn
            ?
            [
                ("Email:Enabled", "true"),
                ("Email:SmtpHost", "smtp.example.test"),
                ("Email:FromAddress", "no-reply@ledger.example.test"),
            ]
            : [];

        return fixture.CreateHost(
            [
                ("Web:Enabled", "true"),
                ("Web:PublicBaseUrl", "https://ledger.example.test"),
                ("Auth:LoginRateLimitPerMinute", "200"),
                .. email,
                .. overrides,
            ])
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<IEmailSender>(recorder)));
    }

    /// <summary>The page minus its one per-response value, the antiforgery token.</summary>
    private static string WithoutAntiforgery(string html) => AntiforgeryValue().Replace(html, "value=\"\"");

    [GeneratedRegex("value=\"CfDJ8[^\"]*\"")]
    private static partial Regex AntiforgeryValue();

    private static string TokenIn(OutgoingEmail message)
    {
        var match = ResetLink().Match(message.Content.Body);
        Assert.True(match.Success, "No reset link in the message.");
        return match.Groups[1].Value;
    }

    [GeneratedRegex("https://ledger\\.example\\.test/portal/reset#token=([A-Za-z0-9_-]+)")]
    private static partial Regex ResetLink();

    private static Task<HttpResponseMessage> RedeemAsync(WebApplicationFactory<Program> host, string token) =>
        host.CreateClient().PostAsJsonAsync("/auth/password/reset", new ResetPasswordRequest(token, NewPassword));

    private static async Task<string> AdminResetAsync(HttpClient admin, int userId)
    {
        var response = await admin.PostAsync($"/admin/users/{userId}/password-reset", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PasswordResetTokenResponse>())!.Token;
    }

    private async Task<PasswordReset> SingleResetAsync(int userId)
    {
        using var db = fixture.CreateDbContext();
        return await db.PasswordResets.AsNoTracking().SingleAsync(r => r.UserId == userId);
    }

    private async Task<int> ResetCountAsync(int userId)
    {
        using var db = fixture.CreateDbContext();
        return await db.PasswordResets.CountAsync(r => r.UserId == userId);
    }

    private async Task<int> SeedUserAsync(string email, bool isAdmin = false, bool disabled = false, bool withPassword = true)
    {
        using var db = fixture.CreateDbContext();
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = withPassword ? new PasswordService().Hash(Password) : null,
            IsAdmin = isAdmin,
            DisabledAtUtc = disabled ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static Task<HttpResponseMessage> DeviceLoginAsync(WebApplicationFactory<Program> host, string email, string password) =>
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
