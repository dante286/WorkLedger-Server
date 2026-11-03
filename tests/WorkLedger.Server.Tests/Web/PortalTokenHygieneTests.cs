using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data.Entities;
using WorkLedger.Server.Tests.Auth;

namespace WorkLedger.Server.Tests.Web;

/// <summary>
/// S11 item 11: <b>no invite or reset token ever reaches a log line</b>, across the whole life of each
/// token, entirely through the portal — minted on the operator pages, carried the way a browser carries a
/// link (the fragment never arrives, so the page is loaded bare), and spent by posting the form.
/// <c>PortalAdminPagesTests</c> covers the same tokens spent through the JSON routes instead.
/// <para>
/// <b>The capture sink is opened to <c>Trace</c> for every category</b>, overriding the host's own
/// filters, so a framework component that echoed a form value at Debug level would fail this test rather
/// than hide below the production threshold. That is stricter than any deployment runs, deliberately: an
/// operator turning logging up to chase a problem must not start writing credentials to disk.
/// </para>
/// <para>
/// The same sink also covers the signed-in forms that take a password or an address: MVC's Trace-level
/// "executing handler method … with arguments" line prints every handler argument, so those values must
/// arrive as bound properties instead.
/// </para>
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public partial class PortalTokenHygieneTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string Admin = "admin@example.com";
    private const string Sam = "sam@example.com";
    private const string Invited = "invited@example.com";
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a brand new passphrase";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task InviteAndResetTokens_NeverReachTheLog_FromIssueToRedemption()
    {
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        var logs = new CapturedLogs();
        using var baseHost = fixture.CreateHost(
            ("Web:Enabled", "true"),
            ("Web:PublicBaseUrl", "https://ledger.example.test"),
            ("Auth:LoginRateLimitPerMinute", "200"));
        using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging
            .AddProvider(logs)
            .AddFilter<CapturedLogs>(category: null, LogLevel.Trace)));

        var operatorBrowser = PortalBrowser.Open(host);
        Assert.Equal(HttpStatusCode.Redirect, (await operatorBrowser.SignInAsync(Admin, Password)).StatusCode);

        var invite = LinkToken(InviteLink(), await (await operatorBrowser.SubmitAsync(
            "/portal/admin/invites?handler=Create", ("Email", Invited))).Content.ReadAsStringAsync());
        var reset = LinkToken(ResetLink(), await (await operatorBrowser.SubmitAsync(
            "/portal/admin/users?handler=Reset", ("userId", samId.ToString()))).Content.ReadAsStringAsync());

        var newcomer = PortalBrowser.Open(host);
        var registered = await newcomer.SubmitAsync(
            "/portal/register",
            ("InviteToken", invite), ("Email", Invited), ("DisplayName", "Invited"),
            ("Password", Password), ("ConfirmPassword", Password));
        var redeemed = await PortalBrowser.Open(host).SubmitAsync(
            "/portal/reset", ("Token", reset), ("Password", NewPassword), ("ConfirmPassword", NewPassword));

        // Each flow really completed, so the assertions below are about a log that saw all of it.
        Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, redeemed.StatusCode);
        using (var db = fixture.CreateDbContext())
        {
            Assert.NotNull((await db.Invites.SingleAsync()).RedeemedAtUtc);
            Assert.NotNull((await db.PasswordResets.SingleAsync()).RedeemedAtUtc);
        }

        Assert.Contains(logs.Lines, l => l.StartsWith("RequestLog:") && l.Contains("POST /portal/register"));
        Assert.Contains(logs.Lines, l => l.StartsWith("RequestLog:") && l.Contains("POST /portal/reset"));
        Assert.Contains(logs.Lines, l => l.Contains("create-invite"));
        Assert.Contains(logs.Lines, l => l.Contains("issue-password-reset"));
        // The host filters Microsoft.AspNetCore at Warning; seeing its lines proves the override took.
        Assert.Contains(logs.Lines, l => l.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal));

        Assert.DoesNotContain(logs.Lines, l => l.Contains(invite, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, l => l.Contains(reset, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PasswordsAndAddresses_PostedToTheSignedInForms_NeverReachTheLog()
    {
        const string Pat = "pat@example.com";
        const string PatPassword = "pat's own long passphrase";
        const string Victim = "victim@example.com";
        const string Invitee = "invitee@example.com";
        const string Confirm = "the new passphrase, again";
        await SeedUserAsync(Admin, isAdmin: true);
        var samId = await SeedUserAsync(Sam);
        var patId = await SeedUserAsync(Pat, password: PatPassword);
        var victimId = await SeedUserAsync(Victim);
        var logs = new CapturedLogs();
        using var baseHost = fixture.CreateHost(
            ("Web:Enabled", "true"),
            ("Web:PublicBaseUrl", "https://ledger.example.test"),
            ("Auth:LoginRateLimitPerMinute", "200"));
        using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging
            .AddProvider(logs)
            .AddFilter<CapturedLogs>(category: null, LogLevel.Trace)));

        var sam = PortalBrowser.Open(host);
        Assert.Equal(HttpStatusCode.Redirect, (await sam.SignInAsync(Sam, Password)).StatusCode);
        var mismatched = await sam.SubmitAsync(
            "/portal/account/security?handler=Password",
            ("currentPassword", Password), ("newPassword", NewPassword), ("confirmPassword", Confirm));
        var changed = await sam.SubmitAsync(
            "/portal/account/security?handler=Password",
            ("currentPassword", Password), ("newPassword", NewPassword), ("confirmPassword", NewPassword));

        var pat = PortalBrowser.Open(host);
        Assert.Equal(HttpStatusCode.Redirect, (await pat.SignInAsync(Pat, PatPassword)).StatusCode);
        var deletedOwn = await pat.SubmitAsync(
            "/portal/account/delete", ("password", PatPassword), ("confirmEmail", Pat));

        var operatorBrowser = PortalBrowser.Open(host);
        Assert.Equal(HttpStatusCode.Redirect, (await operatorBrowser.SignInAsync(Admin, Password)).StatusCode);
        var invited = await operatorBrowser.SubmitAsync(
            "/portal/admin/invites?handler=Create", ("email", Invitee), ("note", "for the new starter"));
        var deletedOther = await operatorBrowser.SubmitAsync(
            "/portal/admin/users?handler=Delete", ("userId", victimId.ToString()), ("confirmEmail", Victim));

        // Each form really ran its handler, so the log below saw every value posted to it.
        Assert.Contains("don&#x27;t match", await mismatched.Content.ReadAsStringAsync());
        Assert.Contains("Your password has been changed", await changed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, deletedOwn.StatusCode);
        Assert.Contains("register#invite=", await invited.Content.ReadAsStringAsync());
        Assert.Contains($"Deleted {Victim}", await deletedOther.Content.ReadAsStringAsync());
        using (var db = fixture.CreateDbContext())
        {
            Assert.NotEqual(PasswordVerification.Failed, new PasswordService().Verify(
                (await db.Users.SingleAsync(u => u.Id == samId)).PasswordHash!, NewPassword));
            Assert.False(await db.Users.AnyAsync(u => u.Id == patId || u.Id == victimId));
            Assert.Equal(Invitee.ToUpperInvariant(), (await db.Invites.SingleAsync()).NormalizedEmail);
        }

        // The handler-execution lines exist at this level, so their absence of values is a real check.
        Assert.Contains(logs.Lines, l => l.Contains("Executing handler method", StringComparison.Ordinal));

        foreach (var line in logs.Lines)
        {
            foreach (var secret in new[] { Password, NewPassword, Confirm, PatPassword, Pat, Victim, Invitee })
            {
                Assert.False(line.Contains(secret, StringComparison.OrdinalIgnoreCase), "LEAK: " + line);
            }
        }
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

    private async Task<int> SeedUserAsync(string email, bool isAdmin = false, string password = Password)
    {
        using var db = fixture.CreateDbContext();
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = new PasswordService().Hash(password),
            IsAdmin = isAdmin,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}
