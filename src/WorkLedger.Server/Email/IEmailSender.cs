namespace WorkLedger.Server.Email;

/// <summary>
/// Hands one message to the relay. <see cref="SmtpEmailSender"/> is the only production implementation;
/// tests replace it with a recording fake, and the real one is covered against Mailpit.
/// <para>
/// A failure is thrown, not returned. The two callers want different things from it:
/// <see cref="EmailDeliveryService"/> classifies it and logs only its type and SMTP status codes, while the
/// portal's test-message page shows the relay's own reply text to the admin who sent it. Nothing else may
/// show or log that text — see <see cref="EmailDeliveryService"/> for why.
/// </para>
/// </summary>
public interface IEmailSender
{
    Task SendAsync(OutgoingEmail message, CancellationToken cancellationToken);
}
