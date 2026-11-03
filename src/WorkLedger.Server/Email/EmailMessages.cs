using System.Globalization;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Email;

/// <summary>
/// Every message this server sends, as plain text: one method per <see cref="EmailKind"/>.
/// <para>
/// <b>Links are built only through <see cref="PortalLinks"/></b>, so only from <c>Web:PublicBaseUrl</c> and
/// never from a request's <c>Host</c> header.
/// </para>
/// <para>
/// <b>Every message with a link also carries its token on a line of its own</b>, with the page to paste it
/// into. Some mail security gateways rewrite links and can drop the fragment the token rides in; S11 gave
/// every onboarding page a paste field for exactly that, and the message points people at it.
/// </para>
/// <para>
/// <b>Emailing these links is safe only because no GET spends a token</b> (S11). Corporate mail scanners open
/// every link they see before the recipient does; since the token is in the fragment and only a POST spends
/// it, scanning burns nothing. <b>Never add a link to a message that spends or reveals a token on GET</b> —
/// the scanner would use it up, or put it in somebody's access log, before the recipient ever saw it.
/// </para>
/// <para>
/// Times are written in UTC with an invariant format: the runtime image runs invariant-globalization, and a
/// message has no reader's time zone to localize into.
/// </para>
/// </summary>
public static class EmailMessages
{
    public static EmailContent Invite(PortalLinks links, string token, DateTime expiresAtUtc) => new(
        "You've been invited to WorkLedger",
        $"""
        You've been invited to create a WorkLedger account.

        Open this link to choose your password:

        {links.Invite(token)}

        The invite can be used once, and expires {Utc(expiresAtUtc)}.

        If the link doesn't open the form filled in, go to {links.RegisterPage} and paste this code:

        {token}

        If you weren't expecting this, you can ignore it.
        """);

    public static EmailContent AdminPasswordReset(PortalLinks links, string token, DateTime expiresAtUtc) => new(
        "Reset your WorkLedger password",
        $"""
        An administrator has issued a password reset for your WorkLedger account.

        Open this link to choose a new password:

        {links.PasswordReset(token)}

        {ResetFooter(links, token, expiresAtUtc)}
        """);

    public static EmailContent SelfServicePasswordReset(PortalLinks links, string token, DateTime expiresAtUtc) => new(
        "Reset your WorkLedger password",
        $"""
        Someone asked to reset the password for your WorkLedger account. If it was you, open this link to
        choose a new password:

        {links.PasswordReset(token)}

        {ResetFooter(links, token, expiresAtUtc)}

        If you didn't ask for this, you can ignore it: your password has not changed.
        """);

    /// <summary>
    /// The security notice. <b>No link and no token</b>: nothing in it can be acted on from the message
    /// itself, so a forged copy of it gets an attacker nothing either.
    /// </summary>
    public static EmailContent PasswordChanged(DateTime changedAtUtc) => new(
        "Your WorkLedger password was changed",
        $"""
        The password for your WorkLedger account was changed at {Utc(changedAtUtc)}.

        If this was you, there is nothing to do.

        If this wasn't you, use "Forgot your password?" on the sign-in page, or contact your administrator.
        """);

    public static EmailContent Test(EmailOptions options) => new(
        "WorkLedger test message",
        $"""
        This is a test message from WorkLedger.

        It was relayed through {options.SmtpHost}:{options.SmtpPort.ToString(CultureInfo.InvariantCulture)} ({options.SmtpSecurity}). If you can read it, this server can send email.
        """);

    private static string ResetFooter(PortalLinks links, string token, DateTime expiresAtUtc) =>
        $"""
        The link can be used once, and expires {Utc(expiresAtUtc)}. Using it signs you out everywhere.

        If the link doesn't open the form filled in, go to {links.ResetPage} and paste this code:

        {token}
        """;

    private static string Utc(DateTime value)
    {
        // timestamptz columns come back as Utc; anything Unspecified is treated as UTC, never as local —
        // the rule PortalFormat follows.
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    }
}
