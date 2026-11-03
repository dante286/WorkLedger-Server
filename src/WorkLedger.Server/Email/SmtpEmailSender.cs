using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace WorkLedger.Server.Email;

/// <summary>
/// <see cref="IEmailSender"/> over SMTP, on MailKit.
/// <para>
/// <b>One connection per message.</b> Volume here is a handful a day; a pooled connection would be one more
/// piece of state that can go stale between messages, for no saving anyone would notice.
/// </para>
/// <para>
/// <b>NEVER attach MailKit's <c>ProtocolLogger</c></b>, not even "just to debug a relay". It records the whole
/// SMTP conversation: the message body, which carries a live invite or reset token, and the <c>AUTH</c>
/// exchange, which carries the SMTP password in base64. The portal's test-message page shows the relay's
/// reply, which is the diagnostic an operator actually needs.
/// </para>
/// <para>
/// <b>Plain text only.</b> No HTML part, no tracking, no remote images: nothing to template, nothing to
/// escape, and nothing that tells anyone a message was opened.
/// </para>
/// <para>
/// Every message carries <c>Auto-Submitted: auto-generated</c> (RFC 3834), which stops well-behaved vacation
/// responders replying to <see cref="EmailOptions.FromAddress"/>.
/// </para>
/// <para>
/// Certificate validation is MailKit's default and is never overridden (see <see cref="EmailOptions"/>).
/// </para>
/// </summary>
public sealed class SmtpEmailSender(EmailOptions options) : IEmailSender
{
    /// <summary>MailKit's default is two minutes, which is a long time to hold a portal request open.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(OutgoingEmail message, CancellationToken cancellationToken)
    {
        // EmailOptions.Resolve guarantees all three once enabled; null here means email is off.
        if (!options.Enabled || options.SmtpHost is null || options.FromAddress is null)
        {
            throw new InvalidOperationException("Email is not configured (Email:Enabled is off).");
        }

        using var mime = Build(message, options.FromAddress);
        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };

        await client.ConnectAsync(options.SmtpHost, options.SmtpPort, SocketOptions(options.SmtpSecurity), cancellationToken);
        if (options.SmtpUsername is not null)
        {
            await client.AuthenticateAsync(options.SmtpUsername, options.SmtpPassword ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }

    private MimeMessage Build(OutgoingEmail message, string fromAddress)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, fromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Content.Subject;
        mime.Headers.Add("Auto-Submitted", "auto-generated");
        mime.Body = new TextPart("plain") { Text = message.Content.Body };
        return mime;
    }

    /// <summary>
    /// <see cref="EmailSecurity.StartTls"/> is <see cref="SecureSocketOptions.StartTls"/>, which REQUIRES the
    /// upgrade. Never <c>StartTlsWhenAvailable</c> or <c>Auto</c>: see <see cref="EmailOptions"/>.
    /// </summary>
    private static SecureSocketOptions SocketOptions(EmailSecurity security) => security switch
    {
        EmailSecurity.StartTls => SecureSocketOptions.StartTls,
        EmailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        EmailSecurity.None => SecureSocketOptions.None,
        _ => throw new ArgumentOutOfRangeException(nameof(security), security, null),
    };
}
