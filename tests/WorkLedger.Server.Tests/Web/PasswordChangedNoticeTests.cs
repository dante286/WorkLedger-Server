using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data.Entities;
using WorkLedger.Server.Email;
using WorkLedger.Server.Tests.Auth;
using WorkLedger.Server.Tests.Email;

namespace WorkLedger.Server.Tests.Web;

/// <summary>
/// S12 group D: "your password was changed". Every route that changes an existing password sends exactly
/// one notice, with no link and no token, including the JSON compatibility route <c>POST /auth/password</c>
/// (decided in WL-213). Nothing else sends one, and a notice that cannot be queued never fails the change.
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public class PasswordChangedNoticeTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string Sam = "Sam@Example.com";
    private const string Admin = "admin@example.com";
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a brand new passphrase";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ChangingThePasswordInThePortal_SendsOneNotice()
    {
        var samId = await SeedUserAsync(Sam);
        using var host = Host(emailOn: true, out var mail);
        var browser = await SignedInAsync(host, Sam);

        var response = await browser.SubmitAsync(
            "/portal/account/security?handler=Password",
            ("currentPassword", Password), ("newPassword", NewPassword), ("confirmPassword", NewPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertIsTheNotice(Assert.Single(await mail.WaitForAsync()), samId);
        await mail.AssertCountStaysAsync(1);
    }

    /// <summary>The compatibility route sends it too, and answers exactly as it always has.</summary>
    [Fact]
    public async Task ChangingThePasswordThroughTheJsonRoute_SendsOneNotice()
    {
        var samId = await SeedUserAsync(Sam);
        using var host = Host(emailOn: true, out var mail);
        var device = Bearer(host, await DeviceTokenAsync(host, Sam));

        var response = await device.PostAsJsonAsync("/auth/password", new ChangePasswordRequest(Password, NewPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await response.Content.ReadFromJsonAsync<LoginResponse>())?.Token);
        AssertIsTheNotice(Assert.Single(await mail.WaitForAsync()), samId);
        await mail.AssertCountStaysAsync(1);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("portal")]
    public async Task RedeemingAReset_SendsOneNotice(string where)
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        using var host = Host(emailOn: true, out var mail);
        var token = await AdminResetAsync(host, samId);

        var redeemed = where == "json"
            ? await host.CreateClient().PostAsJsonAsync("/auth/password/reset", new ResetPasswordRequest(token, NewPassword))
            : await PortalBrowser.Open(host).SubmitAsync(
                "/portal/reset", ("Token", token), ("Password", NewPassword), ("ConfirmPassword", NewPassword));

        Assert.True(redeemed.IsSuccessStatusCode || redeemed.StatusCode == HttpStatusCode.Redirect);
        var notice = Assert.Single(await mail.WaitForAsync());
        AssertIsTheNotice(notice, samId);
        Assert.DoesNotContain(token, notice.Content.Body);
        await mail.AssertCountStaysAsync(1);
    }

    /// <summary>A refused change, signing out everywhere, and deleting the account change no credential worth a notice.</summary>
    [Fact]
    public async Task NothingElse_SendsANotice()
    {
        await SeedUserAsync(Sam);
        using var host = Host(emailOn: true, out var mail);
        var device = Bearer(host, await DeviceTokenAsync(host, Sam));

        var wrong = await device.PostAsJsonAsync("/auth/password", new ChangePasswordRequest("not my password", NewPassword));
        var logoutAll = await device.PostAsync("/auth/logout-all", null);
        var again = Bearer(host, await DeviceTokenAsync(host, Sam));
        var delete = await again.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest(Password, Sam)),
        });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.True(logoutAll.IsSuccessStatusCode);
        Assert.True(delete.IsSuccessStatusCode);
        await mail.AssertNoneAsync();
    }

    [Fact]
    public async Task WithEmailOff_TheChangeWorks_AndNothingIsSent()
    {
        await SeedUserAsync(Sam);
        using var host = Host(emailOn: false, out var mail);
        var device = Bearer(host, await DeviceTokenAsync(host, Sam));

        var response = await device.PostAsJsonAsync("/auth/password", new ChangePasswordRequest(Password, NewPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await mail.AssertNoneAsync();
    }

    /// <summary>
    /// The relay is stuck and the queue is full, so the notice is dropped — and the password still changes.
    /// Capacity 1: the worker holds one notice, the queue holds the next, and the third has nowhere to go.
    /// </summary>
    [Fact]
    public async Task ADroppedNotice_NeverFailsTheChange()
    {
        await SeedUserAsync(Sam);
        var relay = new TaskCompletionSource();
        using var host = Host(emailOn: true, out var mail, ("Email:QueueCapacity", "1"));
        mail.OnSend = (_, _) => relay.Task;

        var passwords = new[] { Password, NewPassword, "a third passphrase here", "and a fourth one too" };
        for (var i = 0; i < passwords.Length - 1; i++)
        {
            var device = Bearer(host, await DeviceTokenAsync(host, Sam, passwords[i]));
            var response = await device.PostAsJsonAsync("/auth/password", new ChangePasswordRequest(passwords[i], passwords[i + 1]));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            if (i == 0)
            {
                await mail.WaitForAsync();
            }
        }

        relay.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await DeviceLoginAsync(host, Sam, passwords[^1])).StatusCode);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static void AssertIsTheNotice(OutgoingEmail message, int userId)
    {
        Assert.Equal(EmailKind.PasswordChanged, message.Kind);
        Assert.Equal(Sam, message.To);
        Assert.Equal(userId, message.UserId);
        Assert.DoesNotContain("http", message.Content.Body);
        Assert.DoesNotContain("#", message.Content.Body);
        Assert.Contains("UTC", message.Content.Body);
    }

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

    private static async Task<PortalBrowser> SignedInAsync(WebApplicationFactory<Program> host, string email)
    {
        var browser = PortalBrowser.Open(host);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.SignInAsync(email, Password)).StatusCode);
        return browser;
    }

    private static async Task<string> AdminResetAsync(WebApplicationFactory<Program> host, int userId)
    {
        var admin = Bearer(host, await DeviceTokenAsync(host, Admin));
        var response = await admin.PostAsync($"/admin/users/{userId}/password-reset", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PasswordResetTokenResponse>())!.Token;
    }

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

    private static Task<HttpResponseMessage> DeviceLoginAsync(WebApplicationFactory<Program> host, string email, string password) =>
        host.CreateClient().PostAsJsonAsync("/auth/login", new LoginRequest(email, password, "device-a", null));

    private static async Task<string> DeviceTokenAsync(WebApplicationFactory<Program> host, string email, string password = Password)
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
