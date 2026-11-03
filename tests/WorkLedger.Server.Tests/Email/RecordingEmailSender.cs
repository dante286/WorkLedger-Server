using System.Collections.Concurrent;
using WorkLedger.Server.Email;

namespace WorkLedger.Server.Tests.Email;

/// <summary>
/// The <see cref="IEmailSender"/> every S12 host test uses in place of MailKit: it records each attempt, and
/// <see cref="OnSend"/> can make an attempt fail or hold it open. Messages queued through
/// <see cref="EmailOutbox"/> arrive here from the real <see cref="EmailDeliveryService"/>, asynchronously,
/// so a test waits for them with <see cref="WaitForAsync"/>.
/// </summary>
internal sealed class RecordingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<OutgoingEmail> _attempts = new();

    /// <summary>Runs on each attempt after it is recorded: throw to fail it, await to hold it.</summary>
    public Func<OutgoingEmail, CancellationToken, Task>? OnSend { get; set; }

    public IReadOnlyCollection<OutgoingEmail> Attempts => _attempts;

    public Task SendAsync(OutgoingEmail message, CancellationToken cancellationToken)
    {
        _attempts.Enqueue(message);
        return OnSend?.Invoke(message, cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>Waits until at least <paramref name="count"/> attempts have been recorded, and returns them.</summary>
    public async Task<IReadOnlyList<OutgoingEmail>> WaitForAsync(int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_attempts.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(_attempts.Count >= count, $"Expected {count} email attempt(s), saw {_attempts.Count}.");
        return [.. _attempts];
    }

    /// <summary>
    /// Asserts nothing was sent, after giving the delivery worker long enough that anything queued would
    /// have arrived.
    /// </summary>
    public Task AssertNoneAsync() => AssertCountStaysAsync(0);

    /// <summary>Asserts the attempt count is still <paramref name="count"/> once the worker has had time to add to it.</summary>
    public async Task AssertCountStaysAsync(int count)
    {
        await Task.Delay(200);
        Assert.Equal(count, _attempts.Count);
    }
}
