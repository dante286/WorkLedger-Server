using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace WorkLedger.Server.Email;

/// <summary>
/// Drains <see cref="EmailOutbox"/> into <see cref="IEmailSender"/>, one message at a time. <b>It exists only
/// while email is on</b>: with <c>Email:Enabled</c> off it is never constructed and never started (see
/// <c>EmailSetup</c>), so an operator who has not turned email on has no mail worker in their process at all.
/// <para>
/// <b>Only transient failures are retried</b>: up to three more attempts, after about 5 seconds, 30 seconds
/// and 2 minutes, for a connection that failed or dropped, a timeout, or an SMTP <b>4xx</b> reply. A
/// <b>5xx</b> reply — a rejected recipient, a refused sender, a revoked API key — is permanent, as is a TLS
/// failure, a relay without the STARTTLS this server requires, or refused credentials; retrying those only
/// repeats them. After the last attempt the message is dropped with one log line. A retrying message holds
/// the line, which at a handful of messages a day costs nothing and keeps one connection to the relay open
/// at a time.
/// </para>
/// <para>
/// <b>Log lines carry the message kind, the user id and the SMTP status — never an address and never the
/// relay's reply text.</b> S8's rule ("the user id and never the email") meets a new problem here: SMTP
/// error replies routinely quote the recipient (<c>550 5.1.1 &lt;bob@example.com&gt;: Recipient address
/// rejected</c>), and MailKit puts that text in the exception's <c>Message</c>. So a failure is logged as its
/// exception type, <c>StatusCode</c> and <c>ErrorCode</c> only, and <b>the exception is never passed to the
/// logger</b> as its <c>exception</c> argument — the console formatter would print its message, and with it
/// the address. The portal's test-message page is the one place the reply text is shown, to the admin who
/// sent that message.
/// </para>
/// </summary>
public sealed class EmailDeliveryService : BackgroundService
{
    /// <summary>The waits before each retry. Its length is the number of retries.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    private readonly EmailOutbox _outbox;
    private readonly IEmailSender _sender;
    private readonly ILogger<EmailDeliveryService> _logger;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    [ActivatorUtilitiesConstructor]
    public EmailDeliveryService(EmailOutbox outbox, IEmailSender sender, ILogger<EmailDeliveryService> logger)
        : this(outbox, sender, logger, DefaultRetryDelays)
    {
    }

    /// <summary>For tests, which cannot wait two and a half minutes to watch a message give up.</summary>
    public EmailDeliveryService(
        EmailOutbox outbox,
        IEmailSender sender,
        ILogger<EmailDeliveryService> logger,
        IReadOnlyList<TimeSpan> retryDelays)
    {
        _outbox = outbox;
        _sender = sender;
        _logger = logger;
        _retryDelays = retryDelays;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(stoppingToken))
            {
                await DeliverAsync(message, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. Whatever is still queued is lost, by design — see EmailOutbox.
        }
    }

    private async Task DeliverAsync(OutgoingEmail message, CancellationToken stoppingToken)
    {
        var attempts = _retryDelays.Count + 1;
        for (var attempt = 1; ; attempt++)
        {
            Exception failure;
            try
            {
                await _sender.SendAsync(message, stoppingToken);
                _logger.LogInformation(
                    "Delivered {Kind} email for user {UserId} to the relay.", message.Kind, UserId(message));
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            var (status, code) = Codes(failure);
            if (!IsTransient(failure) || attempt == attempts)
            {
                // No `exception:` argument, deliberately: see this class's doc.
                _logger.LogWarning(
                    "Dropped {Kind} email for user {UserId} after {Attempts} attempt(s): {ExceptionType} (SMTP status {StatusCode}, error {ErrorCode}){Reason}.",
                    message.Kind, UserId(message), attempt, failure.GetType().Name, status, code,
                    IsTransient(failure) ? "; out of retries" : "; not retried, the failure is permanent");
                return;
            }

            var delay = _retryDelays[attempt - 1];
            _logger.LogWarning(
                "Delivery of {Kind} email for user {UserId} failed on attempt {Attempt} of {Attempts}: {ExceptionType} (SMTP status {StatusCode}, error {ErrorCode}); retrying in {Delay}.",
                message.Kind, UserId(message), attempt, attempts, failure.GetType().Name, status, code, delay);
            await Task.Delay(delay, stoppingToken);
        }
    }

    /// <summary>
    /// Worth another attempt: the relay was unreachable, dropped the connection, timed out, or replied 4xx.
    /// Checked most-specific first, because MailKit's TLS failure is itself an <see cref="IOException"/> and
    /// a certificate that failed validation now will fail it again in two minutes.
    /// </summary>
    internal static bool IsTransient(Exception exception) => exception switch
    {
        SmtpCommandException command => (int)command.StatusCode is >= 400 and < 500,
        SslHandshakeException or AuthenticationException => false,
        SmtpProtocolException or ServiceNotConnectedException => true,
        SocketException or IOException or TimeoutException => true,

        // A cancellation that is not shutdown (shutdown is rethrown before this is asked) is MailKit's own
        // timeout surfacing as one.
        OperationCanceledException => true,
        _ => false,
    };

    /// <summary>The numbers in an SMTP failure — never its text, which may quote the recipient.</summary>
    private static (string Status, string Code) Codes(Exception exception) => exception is SmtpCommandException command
        ? (((int)command.StatusCode).ToString(CultureInfo.InvariantCulture), command.ErrorCode.ToString())
        : ("-", "-");

    private static string UserId(OutgoingEmail message) =>
        message.UserId?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
