using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Email;

/// <summary>
/// The portal's "send a test message to me" (S12 item 10): how an operator checks their relay, because
/// startup deliberately does not contact it (see <see cref="EmailOptions"/>).
/// <para>
/// <b>It sends to the signed-in admin's own address and nobody else's</b>, and <b>bypasses
/// <see cref="EmailOutbox"/></b>: the admin is waiting for the answer, so it goes straight to
/// <see cref="IEmailSender"/> and waits up to <see cref="SmtpEmailSender.Timeout"/>, with no retry.
/// </para>
/// <para>
/// <b>This is the one place the relay's own reply text is surfaced</b>, which
/// <see cref="EmailDeliveryService"/> otherwise never logs or shows because it can quote a recipient's
/// address. Here the reader is the person who configured the relay and the address is their own, and the
/// text is exactly what they need: Resend, for one, says in it that the from address is not on a verified
/// domain, or that the API key was revoked. It is returned to the page and never logged; the audit line
/// (<c>send-test-email</c>) carries ids only.
/// </para>
/// </summary>
public sealed class RelayCheck(
    EmailOptions options,
    IEmailSender sender,
    WorkLedgerDbContext db,
    ILoggerFactory loggerFactory)
{
    public async Task<RelayCheckResult> SendTestAsync(int actorUserId, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("Email is not configured (Email:Enabled is off).");
        }

        var to = await db.Users
            .Where(u => u.Id == actorUserId)
            .Select(u => u.Email)
            .SingleAsync(cancellationToken)
            ?? throw new InvalidOperationException("A signed-in admin always has an email address.");

        AdminOperations.Audit(loggerFactory, actorUserId, "send-test-email", actorUserId);

        // MailKit applies its timeout per operation; this bounds the whole exchange to about the same.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SmtpEmailSender.Timeout);

        try
        {
            await sender.SendAsync(
                new OutgoingEmail(EmailKind.Test, actorUserId, to, EmailMessages.Test(options)), timeout.Token);
            return new RelayCheckResult(to, Delivered: true, StatusCode: null, Reply: null);
        }
        catch (SmtpCommandException exception)
        {
            return new RelayCheckResult(to, false, (int)exception.StatusCode, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RelayCheckResult(
                to, false, null,
                $"No answer from {options.SmtpHost}:{options.SmtpPort} within {SmtpEmailSender.Timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A connection refused, a TLS failure, a relay without the STARTTLS this server requires, or
            // refused credentials: the type says which kind, and the message says why.
            return new RelayCheckResult(to, false, null, $"{exception.GetType().Name}: {exception.Message}");
        }
    }
}

/// <summary>
/// What the relay said to a test message. <see cref="Reply"/> is the relay's own text (or the failure's),
/// for the admin who sent it only — never logged.
/// </summary>
public sealed record RelayCheckResult(string To, bool Delivered, int? StatusCode, string? Reply);
