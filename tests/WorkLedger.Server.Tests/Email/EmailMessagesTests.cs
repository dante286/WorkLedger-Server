using WorkLedger.Server.Email;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Tests.Email;

/// <summary>
/// S12 item 4: links come from <c>Web:PublicBaseUrl</c> with the token in the fragment, every link is
/// backed by the bare token and its paste page, and the password-changed notice carries neither.
/// </summary>
public class EmailMessagesTests
{
    private const string Token = "AbC-123_xyz";
    private static readonly DateTime Expiry = new(2026, 11, 4, 9, 30, 0, DateTimeKind.Utc);

    private static readonly PortalLinks Links =
        new(new WebOptions(true, "https://ledger.example.test", WebOptions.DefaultSessionHours));

    public static TheoryData<string, string, string> Linked => new()
    {
        { "invite", "https://ledger.example.test/portal/register#invite=" + Token, "https://ledger.example.test/portal/register and paste" },
        { "admin-reset", "https://ledger.example.test/portal/reset#token=" + Token, "https://ledger.example.test/portal/reset and paste" },
        { "self-reset", "https://ledger.example.test/portal/reset#token=" + Token, "https://ledger.example.test/portal/reset and paste" },
    };

    [Theory]
    [MemberData(nameof(Linked))]
    public void ALinkedMessage_CarriesTheLink_AndTheTokenOnItsOwnLine(string kind, string link, string pastePage)
    {
        var message = kind switch
        {
            "invite" => EmailMessages.Invite(Links, Token, Expiry),
            "admin-reset" => EmailMessages.AdminPasswordReset(Links, Token, Expiry),
            _ => EmailMessages.SelfServicePasswordReset(Links, Token, Expiry),
        };

        var lines = message.Body.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        Assert.Contains(link, lines);
        Assert.Contains(Token, lines);
        Assert.Contains(pastePage, message.Body);
        Assert.Contains("2026-11-04 09:30 UTC", message.Body);
    }

    [Fact]
    public void ThePasswordChangedNotice_CarriesNoLinkAndNoToken()
    {
        var message = EmailMessages.PasswordChanged(Expiry);

        Assert.DoesNotContain("http", message.Body);
        Assert.DoesNotContain("#", message.Body);
        Assert.Contains("2026-11-04 09:30 UTC", message.Body);
    }
}
