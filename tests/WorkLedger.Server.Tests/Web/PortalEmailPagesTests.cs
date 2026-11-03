using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data.Entities;
using WorkLedger.Server.Email;
using WorkLedger.Server.Tests.Auth;
using WorkLedger.Server.Tests.Email;

namespace WorkLedger.Server.Tests.Web;

/// <summary>
/// S12 group B: operator-sent links. An admin emails an invite or a reset from the portal or
/// <c>/admin/*</c>, the link is still shown once, and <c>/portal/admin/email</c> sends the admin a test
/// message straight to the relay. With email off none of it exists, and a forged request for it is refused
/// without creating anything.
/// <para>
/// Every host here uses <see cref="RecordingEmailSender"/>; messages reach it through the real queue and
/// delivery worker, so these tests cover the path a message really takes, short of MailKit.
/// </para>
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public partial class PortalEmailPagesTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string Admin = "admin@example.com";
    private const string Sam = "sam@example.com";
    private const string Invitee = "Invitee@Example.com";
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a brand new passphrase";
    private const string SmtpPassword = "re_never_render_this_secret";
    private const string UsersPage = "/portal/admin/users";
    private const string InvitesPage = "/portal/admin/invites";
    private const string EmailPage = "/portal/admin/email";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- email off: nothing offered, and nothing created when asked for anyway ------------------

    [Fact]
    public async Task WithEmailOff_NoPageOffersEmail_AndTheEmailPageDoesNotExist()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        await SeedUserAsync(Sam);
        using var host = Host(emailOn: false, out _);
        var browser = await SignedInAsync(host, Admin);

        var invites = await (await browser.GetAsync(InvitesPage)).Content.ReadAsStringAsync();
        var users = await (await browser.GetAsync(UsersPage)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("sendEmail", invites);
        Assert.DoesNotContain("ResetAndEmail", users);
        Assert.DoesNotContain(EmailPage, users);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(EmailPage)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await browser.PostAsync($"{EmailPage}?handler=Test", await browser.FormTokenAsync(UsersPage))).StatusCode);
    }

    [Fact]
    public async Task WithEmailOff_AForgedEmailRequest_IsRefused_AndCreatesNothing()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        using var host = Host(emailOn: false, out _);
        var browser = await SignedInAsync(host, Admin);

        var invite = await browser.SubmitAsync(
            $"{InvitesPage}?handler=Create", ("email", Invitee), ("sendEmail", "true"));
        var reset = await ActAsync(browser, "ResetAndEmail", samId);

        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
        Assert.Contains("Email isn&#x27;t configured", await invite.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Contains("Email isn&#x27;t configured", await reset.Content.ReadAsStringAsync());
        await AssertNothingIssuedAsync();
    }

    [Fact]
    public async Task WithEmailOff_SendEmailOnTheAdminRoutes_Is409_AndCreatesNothing()
    {
        var adminId = await SeedUserAsync(Admin, isAdmin: true);
        using var host = Host(emailOn: false, out _);
        var admin = Bearer(host, await DeviceTokenAsync(host, Admin));

        var invite = await admin.PostAsJsonAsync("/admin/invites", new InviteRequest(Invitee, SendEmail: true));
        var reset = await admin.PostAsJsonAsync(
            $"/admin/users/{adminId}/password-reset", new PasswordResetIssueRequest(SendEmail: true));

        Assert.Equal(HttpStatusCode.Conflict, invite.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, reset.StatusCode);
        Assert.Contains("Email:Enabled", await reset.Content.ReadAsStringAsync());
        await AssertNothingIssuedAsync();
    }

    // ---- invites ------------------------------------------------------------------------------

    /// <summary>
    /// The emailed copy and the shown link are the same token; the invite is bound to the address it went
    /// to, so it registers that address and nobody else, once.
    /// </summary>
    [Fact]
    public async Task AnEmailedInvite_IsBoundToTheAddress_ShownOnce_AndRegistersExactlyOnce()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        using var host = Host(emailOn: true, out var mail);
        var browser = await SignedInAsync(host, Admin);

        var html = await (await browser.SubmitAsync(
            $"{InvitesPage}?handler=Create", ("email", Invitee), ("sendEmail", "true"))).Content.ReadAsStringAsync();
        var token = LinkToken(InviteLink(), html);
        var sent = Assert.Single(await mail.WaitForAsync());

        Assert.Contains($"Queued for delivery to {Invitee}", html);
        Assert.Equal(EmailKind.Invite, sent.Kind);
        Assert.Equal(Invitee, sent.To);
        Assert.Contains(token, sent.Content.Body);
        Assert.Equal(Invitee.ToUpperInvariant(), (await SingleInviteAsync()).NormalizedEmail);

        Assert.False((await RegisterAsync(host, "someone.else@example.com", token)).IsSuccessStatusCode);
        Assert.True((await RegisterAsync(host, Invitee, token)).IsSuccessStatusCode);
        Assert.False((await RegisterAsync(host, Invitee, token)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task EmailingAnOpenInvite_IsRefused_AndCreatesNothing()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        using var host = Host(emailOn: true, out var mail);
        var browser = await SignedInAsync(host, Admin);

        var html = await (await browser.SubmitAsync(
            $"{InvitesPage}?handler=Create", ("email", ""), ("sendEmail", "true"))).Content.ReadAsStringAsync();

        Assert.Contains("enter the address it&#x27;s for", html);
        await AssertNothingIssuedAsync();
        await mail.AssertNoneAsync();
    }

    [Fact]
    public async Task WithEmailOn_TheInvitesPage_OffersTheCheckbox_AndAPlainInviteSendsNothing()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        using var host = Host(emailOn: true, out var mail);
        var browser = await SignedInAsync(host, Admin);

        Assert.Contains("name=\"sendEmail\"", await (await browser.GetAsync(InvitesPage)).Content.ReadAsStringAsync());
        var html = await (await browser.SubmitAsync(
            $"{InvitesPage}?handler=Create", ("email", Invitee))).Content.ReadAsStringAsync();

        LinkToken(InviteLink(), html);
        Assert.DoesNotContain("Queued for delivery", html);
        await mail.AssertNoneAsync();
    }

    // ---- resets -------------------------------------------------------------------------------

    /// <summary>
    /// The same issue path as "Password reset link": the earlier reset dies, the emailed one redeems, and
    /// the audit lines carry ids, never the token.
    /// </summary>
    [Fact]
    public async Task AnEmailedReset_ReachesTheTarget_KillsTheEarlierReset_AndRedeems()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        var logs = new CapturedLogs();
        using var baseHost = Host(emailOn: true, out var mail);
        using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureLogging(l => l.AddProvider(logs)));
        var browser = await SignedInAsync(host, Admin);

        var earlier = LinkToken(ResetLink(), await (await ActAsync(browser, "Reset", samId)).Content.ReadAsStringAsync());
        var html = await (await ActAsync(browser, "ResetAndEmail", samId)).Content.ReadAsStringAsync();
        var token = LinkToken(ResetLink(), html);
        var sent = Assert.Single(await mail.WaitForAsync());

        Assert.Contains($"Queued for delivery to {Sam}", html);
        Assert.Equal(EmailKind.AdminPasswordReset, sent.Kind);
        Assert.Equal(Sam, sent.To);
        Assert.Equal(samId, sent.UserId);
        Assert.Contains(token, sent.Content.Body);

        Assert.False((await RedeemAsync(host, earlier)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await RedeemAsync(host, token)).StatusCode);

        Assert.Contains(logs.Lines, l => l.StartsWith("AdminAction:") && l.Contains("email-password-reset") && l.Contains($"user {samId}"));
        Assert.Equal(2, logs.Lines.Count(l => l.StartsWith("AdminAction:") && l.Contains("issue-password-reset")));
        Assert.DoesNotContain(logs.Lines, l => l.Contains(token) || l.Contains(earlier));
    }

    // ---- the queue ----------------------------------------------------------------------------

    /// <summary>
    /// With the relay stuck and the queue full, both operator pages say so and still show the link — the
    /// fallback decision 5 keeps. Capacity 1: the worker holds one message, the queue holds the next.
    /// </summary>
    [Fact]
    public async Task AFullQueue_IsReported_AndTheLinkIsStillShown()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        var relay = new TaskCompletionSource();
        using var host = Host(emailOn: true, out var mail, ("Email:QueueCapacity", "1"));
        mail.OnSend = (_, _) => relay.Task;
        var browser = await SignedInAsync(host, Admin);

        await browser.SubmitAsync($"{InvitesPage}?handler=Create", ("email", "one@example.com"), ("sendEmail", "true"));
        await mail.WaitForAsync();
        await browser.SubmitAsync($"{InvitesPage}?handler=Create", ("email", "two@example.com"), ("sendEmail", "true"));

        var invite = await (await browser.SubmitAsync(
            $"{InvitesPage}?handler=Create", ("email", "three@example.com"), ("sendEmail", "true"))).Content.ReadAsStringAsync();
        var reset = await (await ActAsync(browser, "ResetAndEmail", samId)).Content.ReadAsStringAsync();
        relay.SetResult();

        Assert.Contains("The mail queue is full", invite);
        LinkToken(InviteLink(), invite);
        Assert.Contains("The mail queue is full", reset);
        LinkToken(ResetLink(), reset);
    }

    // ---- /portal/admin/email ------------------------------------------------------------------

    [Fact]
    public async Task TheEmailPage_ShowsTheSettings_ButNeverThePassword()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        using var host = Host(emailOn: true, out _);
        var browser = await SignedInAsync(host, Admin);

        var html = await (await browser.GetAsync(EmailPage)).Content.ReadAsStringAsync();

        Assert.Contains("smtp.example.test:587", html);
        Assert.Contains("StartTls", html);
        Assert.Contains("As resend, with a password", html);
        Assert.Contains("no-reply@ledger.example.test", html);
        Assert.DoesNotContain(SmtpPassword, html);
        Assert.Contains($"href=\"{EmailPage}\"", await (await browser.GetAsync(UsersPage)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheTestMessage_GoesToTheSignedInAdminOnly_WithoutTheQueue()
    {
        var adminId = await SeedUserAsync(Admin, isAdmin: true);
        await SeedUserAsync(Sam);
        var logs = new CapturedLogs();
        using var baseHost = Host(emailOn: true, out var mail);
        using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureLogging(l => l.AddProvider(logs)));
        var browser = await SignedInAsync(host, Admin);

        var html = await (await browser.SubmitAsync($"{EmailPage}?handler=Test")).Content.ReadAsStringAsync();

        Assert.Contains($"Delivered to the relay for {Admin}", html);
        var sent = Assert.Single(mail.Attempts);
        Assert.Equal(EmailKind.Test, sent.Kind);
        Assert.Equal(Admin, sent.To);
        Assert.Contains(logs.Lines, l => l.StartsWith("AdminAction:") && l.Contains("send-test-email") && l.Contains($"user {adminId}"));
    }

    /// <summary>
    /// The relay's reply — which names the problem, and may quote an address — is shown to the admin who
    /// sent the test, and reaches no log line.
    /// </summary>
    [Fact]
    public async Task AFailedTest_ShowsTheRelaysReply_AndLogsNoneOfIt()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        const string Reply = "5.7.1 <no-reply@ledger.example.test>: sender domain is not verified";
        var logs = new CapturedLogs();
        using var baseHost = Host(emailOn: true, out var mail);
        mail.OnSend = (_, _) => throw new SmtpCommandException(
            SmtpErrorCode.SenderNotAccepted, SmtpStatusCode.MailboxUnavailable,
            MailboxAddress.Parse("no-reply@ledger.example.test"), Reply);
        using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureLogging(l => l.AddProvider(logs)));
        var browser = await SignedInAsync(host, Admin);

        var html = await (await browser.SubmitAsync($"{EmailPage}?handler=Test")).Content.ReadAsStringAsync();

        Assert.Contains("SMTP status 550", html);
        Assert.Contains("sender domain is not verified", html);
        Assert.DoesNotContain(logs.Lines, l => l.Contains("sender domain is not verified"));
    }

    [Fact]
    public async Task TheEmailPage_IsAdminOnly_AndItsTestNeedsAntiforgery()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        await SeedUserAsync(Sam);
        using var host = Host(emailOn: true, out var mail);
        var sam = await SignedInAsync(host, Sam);
        var admin = await SignedInAsync(host, Admin);

        Assert.NotEqual(HttpStatusCode.OK, (await sam.GetAsync(EmailPage)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{EmailPage}?handler=Test", null)).StatusCode);
        await mail.AssertNoneAsync();
    }

    // ---- /admin/* parity ----------------------------------------------------------------------

    [Fact]
    public async Task TheAdminRoutes_EmailOnlyWhenAsked_AndOmitEmailQueuedOtherwise()
    {
        var adminId = await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        using var host = Host(emailOn: true, out var mail);
        var admin = Bearer(host, await DeviceTokenAsync(host, Admin));

        using var plainInvite = await JsonAsync(await admin.PostAsJsonAsync("/admin/invites", new InviteRequest(Invitee)));
        using var plainReset = await JsonAsync(await admin.PostAsync($"/admin/users/{samId}/password-reset", null));
        Assert.False(plainInvite.RootElement.TryGetProperty("emailQueued", out _));
        Assert.False(plainReset.RootElement.TryGetProperty("emailQueued", out _));
        await mail.AssertNoneAsync();

        using var invite = await JsonAsync(await admin.PostAsJsonAsync("/admin/invites", new InviteRequest(Invitee, SendEmail: true)));
        using var reset = await JsonAsync(await admin.PostAsJsonAsync(
            $"/admin/users/{samId}/password-reset", new PasswordResetIssueRequest(SendEmail: true)));
        Assert.True(invite.RootElement.GetProperty("emailQueued").GetBoolean());
        Assert.True(reset.RootElement.GetProperty("emailQueued").GetBoolean());

        var sent = await mail.WaitForAsync(2);
        Assert.Contains(sent, m => m.Kind == EmailKind.Invite && m.To == Invitee
            && m.Content.Body.Contains(invite.RootElement.GetProperty("token").GetString()!));
        Assert.Contains(sent, m => m.Kind == EmailKind.AdminPasswordReset && m.To == Sam
            && m.Content.Body.Contains(reset.RootElement.GetProperty("token").GetString()!));
        Assert.NotEqual(adminId, samId);
    }

    [Fact]
    public async Task SendEmailWithoutAnAddress_OnTheAdminRoute_Is400_AndCreatesNothing()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        using var host = Host(emailOn: true, out _);
        var admin = Bearer(host, await DeviceTokenAsync(host, Admin));

        var response = await admin.PostAsJsonAsync("/admin/invites", new InviteRequest(SendEmail: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingIssuedAsync();
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
                ("Email:SmtpUsername", "resend"),
                ("Email:SmtpPassword", SmtpPassword),
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

    private static Task<HttpResponseMessage> ActAsync(PortalBrowser browser, string handler, int userId) =>
        browser.SubmitAsync($"{UsersPage}?handler={handler}", ("userId", userId.ToString()));

    private static Task<HttpResponseMessage> RegisterAsync(WebApplicationFactory<Program> host, string email, string invite) =>
        host.CreateClient().PostAsJsonAsync("/auth/register", new RegisterUserRequest(email, Password, null, InviteToken: invite));

    private static Task<HttpResponseMessage> RedeemAsync(WebApplicationFactory<Program> host, string token) =>
        host.CreateClient().PostAsJsonAsync("/auth/password/reset", new ResetPasswordRequest(token, NewPassword));

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

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

    private async Task AssertNothingIssuedAsync()
    {
        using var db = fixture.CreateDbContext();
        Assert.Equal(0, await db.Invites.CountAsync());
        Assert.Equal(0, await db.PasswordResets.CountAsync());
    }

    private async Task<Invite> SingleInviteAsync()
    {
        using var db = fixture.CreateDbContext();
        return await db.Invites.AsNoTracking().SingleAsync();
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

    private static async Task<string> DeviceTokenAsync(WebApplicationFactory<Program> host, string email)
    {
        var response = await host.CreateClient().PostAsJsonAsync("/auth/login", new LoginRequest(email, Password, "device-a", null));
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
