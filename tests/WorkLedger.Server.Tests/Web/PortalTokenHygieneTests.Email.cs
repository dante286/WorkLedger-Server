using System.Net;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using WorkLedger.Server.Email;
using WorkLedger.Server.Tests.Email;

namespace WorkLedger.Server.Tests.Web;

/// <summary>
/// S12 item 20's log-hygiene case: with email on, every kind of message the server sends — an emailed
/// invite, an emailed reset, a self-service reset and the password-changed notice — fails at a relay whose
/// reply quotes the recipient, the way real ones do (<c>550 5.1.1 &lt;bob@example.com&gt;: Recipient address
/// rejected</c>). Through all of it, at Trace, no address, no token and none of the reply text reaches a log
/// line; the drops themselves are logged, by kind and user id.
/// </summary>
public partial class PortalTokenHygieneTests
{
    [Fact]
    public async Task WithEmailOn_ARelayQuotingTheRecipient_PutsNoAddressOrTokenInTheLog()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        var logs = new CapturedLogs();
        var mail = new RecordingEmailSender
        {
            OnSend = (message, _) => throw new SmtpCommandException(
                SmtpErrorCode.RecipientNotAccepted,
                SmtpStatusCode.MailboxUnavailable,
                MailboxAddress.Parse(message.To),
                $"5.1.1 <{message.To}>: Recipient address rejected"),
        };
        using var baseHost = fixture.CreateHost(
            ("Web:Enabled", "true"),
            ("Web:PublicBaseUrl", "https://ledger.example.test"),
            ("Auth:LoginRateLimitPerMinute", "200"),
            ("Email:Enabled", "true"),
            ("Email:SmtpHost", "smtp.example.test"),
            ("Email:FromAddress", "no-reply@ledger.example.test"));
        using var host = baseHost.WithWebHostBuilder(builder => builder
            .ConfigureTestServices(services => services.AddSingleton<IEmailSender>(mail))
            .ConfigureLogging(logging => logging
                .AddProvider(logs)
                .AddFilter<CapturedLogs>(category: null, LogLevel.Trace)));

        var operatorBrowser = PortalBrowser.Open(host);
        Assert.Equal(HttpStatusCode.Redirect, (await operatorBrowser.SignInAsync(Admin, Password)).StatusCode);

        var invite = LinkToken(InviteLink(), await (await operatorBrowser.SubmitAsync(
            "/portal/admin/invites?handler=Create", ("email", Invited), ("sendEmail", "true"))).Content.ReadAsStringAsync());
        var adminReset = LinkToken(ResetLink(), await (await operatorBrowser.SubmitAsync(
            "/portal/admin/users?handler=ResetAndEmail", ("userId", samId.ToString()))).Content.ReadAsStringAsync());
        await mail.WaitForAsync(2);

        await PortalBrowser.Open(host).SubmitAsync("/portal/forgot", ("Address", Sam));
        var selfService = SelfServiceToken().Match((await mail.WaitForAsync(3))[2].Content.Body).Groups[1].Value;

        var redeemed = await PortalBrowser.Open(host).SubmitAsync(
            "/portal/reset", ("Token", selfService), ("Password", NewPassword), ("ConfirmPassword", NewPassword));
        Assert.Equal(HttpStatusCode.Redirect, redeemed.StatusCode);

        var attempts = await mail.WaitForAsync(4);
        Assert.Equal(
            [EmailKind.Invite, EmailKind.AdminPasswordReset, EmailKind.SelfServicePasswordReset, EmailKind.PasswordChanged],
            attempts.Select(a => a.Kind));
        for (var i = 0; i < 50 && logs.Lines.Count(l => l.Contains("Dropped")) < 4; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(4, logs.Lines.Count(l => l.Contains("Dropped") && l.Contains("SMTP status 550")));
        Assert.Contains(logs.Lines, l => l.Contains($"Dropped PasswordChanged email for user {samId}"));
        foreach (var line in logs.Lines)
        {
            foreach (var secret in new[] { Admin, Sam, Invited, invite, adminReset, selfService, "Recipient address rejected" })
            {
                Assert.False(line.Contains(secret, StringComparison.OrdinalIgnoreCase), $"Leaked \"{secret}\": {line}");
            }
        }
    }

    [GeneratedRegex("https://ledger\\.example\\.test/portal/reset#token=([A-Za-z0-9_-]+)")]
    private static partial Regex SelfServiceToken();
}
