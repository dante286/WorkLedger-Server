using System.Collections.Concurrent;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using WorkLedger.Server.Email;
using WorkLedger.Server.Tests.Web;

namespace WorkLedger.Server.Tests.Email;

/// <summary>
/// S12 items 3 and 6: what <see cref="EmailDeliveryService"/> retries, what it gives up on, and that no log
/// line it writes carries an address or the relay's reply text. Unit-level, against a scripted sender, with
/// the retry delays at zero.
/// </summary>
public class EmailDeliveryServiceTests
{
    private const string Recipient = "bob@example.test";
    private const string Token = "raw-token-that-must-never-be-logged";

    private static readonly EmailOptions Enabled = EmailOptions.Disabled with
    {
        Enabled = true,
        SmtpHost = "smtp.example.test",
        FromAddress = "no-reply@ledger.example.test",
        QueueCapacity = 2,
    };

    public static TheoryData<string, int> Failures => new()
    {
        // Transient: every attempt is made.
        { "451", 4 },
        { "socket", 4 },
        { "timeout", 4 },
        { "protocol", 4 },

        // Permanent: one attempt and done.
        { "550", 1 },
        { "535-auth", 1 },
        { "tls", 1 },
        { "no-starttls", 1 },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailure_IsRetriedOnlyWhenTransient(string failure, int expectedAttempts)
    {
        var sender = new ScriptedSender(_ => throw Failure(failure));
        var logs = new CapturedLogs();

        await RunUntilAsync(sender, logs, Message(), () => logs.Lines.Any(line => line.Contains("Dropped")));

        Assert.Equal(expectedAttempts, sender.Attempts);
        var dropped = Assert.Single(logs.Lines, line => line.Contains("Dropped"));
        Assert.Contains($"after {expectedAttempts} attempt(s)", dropped);
        Assert.Contains("for user 42", dropped);
    }

    [Fact]
    public async Task ATransientFailure_ThenSuccess_IsDeliveredOnTheRetry()
    {
        var sender = new ScriptedSender(attempt => attempt == 1 ? throw Failure("451") : Task.CompletedTask);
        var logs = new CapturedLogs();

        await RunUntilAsync(sender, logs, Message(), () => logs.Lines.Any(line => line.Contains("Delivered")));

        Assert.Equal(2, sender.Attempts);
        Assert.DoesNotContain(logs.Lines, line => line.Contains("Dropped"));
    }

    /// <summary>
    /// The reply MailKit puts in <c>SmtpCommandException.Message</c> quotes the recipient. None of it — nor
    /// the recipient, nor the body's token — may reach a log line; the status and error codes do.
    /// </summary>
    [Theory]
    [InlineData("451")]
    [InlineData("550")]
    public async Task NoLogLine_CarriesTheAddress_TheReplyText_OrTheToken(string failure)
    {
        var sender = new ScriptedSender(_ => throw Failure(failure));
        var logs = new CapturedLogs();

        await RunUntilAsync(sender, logs, Message(), () => logs.Lines.Any(line => line.Contains("Dropped")));

        Assert.NotEmpty(logs.Lines);
        foreach (var line in logs.Lines)
        {
            Assert.DoesNotContain(Recipient, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Recipient address rejected", line);
            Assert.DoesNotContain(Token, line);
        }

        Assert.Contains(logs.Lines, line => line.Contains($"SMTP status {failure}") && line.Contains("SmtpCommandException"));
    }

    [Fact]
    public void AFullQueue_RefusesTheMessage_WithoutWaiting()
    {
        var outbox = new EmailOutbox(Enabled);

        Assert.True(outbox.TryEnqueue(Message()));
        Assert.True(outbox.TryEnqueue(Message()));
        Assert.False(outbox.TryEnqueue(Message()));
    }

    [Fact]
    public void AnOutgoingEmail_PrintsNeitherItsRecipientNorItsBody()
    {
        var printed = Message().ToString();

        Assert.DoesNotContain(Recipient, printed);
        Assert.DoesNotContain(Token, printed);
        Assert.Contains("AdminPasswordReset", printed);
    }

    [Fact]
    public async Task WithEmailOff_TheWorkerReturnsAtOnce()
    {
        var sender = new ScriptedSender(_ => Task.CompletedTask);
        using var service = new EmailDeliveryService(
            EmailOptions.Disabled, new EmailOutbox(EmailOptions.Disabled), sender,
            LoggerFactory.Create(_ => { }).CreateLogger<EmailDeliveryService>(), []);

        await service.StartAsync(CancellationToken.None);

        // .NET 10 runs ExecuteAsync off the starting thread, so "at once" is "without being stopped".
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(0, sender.Attempts);
    }

    private static OutgoingEmail Message() =>
        new(EmailKind.AdminPasswordReset, 42, Recipient, new EmailContent("Reset", $"Your code: {Token}"));

    private static Exception Failure(string kind) => kind switch
    {
        "451" => new SmtpCommandException(
            SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.ErrorInProcessing, MailboxAddress.Parse(Recipient),
            $"<{Recipient}>: Recipient address rejected: greylisted"),
        "550" => new SmtpCommandException(
            SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, MailboxAddress.Parse(Recipient),
            $"5.1.1 <{Recipient}>: Recipient address rejected"),
        "socket" => new SocketException((int)SocketError.ConnectionRefused),
        "timeout" => new TimeoutException(),
        "protocol" => new SmtpProtocolException("The SMTP server has unexpectedly disconnected."),
        "535-auth" => new AuthenticationException("535: Authentication credentials invalid"),
        "tls" => new SslHandshakeException("certificate rejected"),
        "no-starttls" => new NotSupportedException("The SMTP server does not support the STARTTLS extension."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static async Task RunUntilAsync(ScriptedSender sender, CapturedLogs logs, OutgoingEmail message, Func<bool> done)
    {
        var outbox = new EmailOutbox(Enabled);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        using var service = new EmailDeliveryService(
            Enabled, outbox, sender, factory.CreateLogger<EmailDeliveryService>(),
            [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);

        await service.StartAsync(CancellationToken.None);
        Assert.True(outbox.TryEnqueue(message));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await service.StopAsync(CancellationToken.None);
        Assert.True(done(), "the worker never reached the expected outcome");
    }

    private sealed class ScriptedSender(Func<int, Task> onAttempt) : IEmailSender
    {
        private readonly ConcurrentQueue<OutgoingEmail> _attempts = new();

        public int Attempts => _attempts.Count;

        public Task SendAsync(OutgoingEmail message, CancellationToken cancellationToken)
        {
            _attempts.Enqueue(message);
            return onAttempt(_attempts.Count);
        }
    }
}
