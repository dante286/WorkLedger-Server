using System.Threading.Channels;

namespace WorkLedger.Server.Email;

/// <summary>
/// Outgoing mail waiting for <see cref="EmailDeliveryService"/>: a bounded in-process
/// <see cref="Channel{T}"/>, sized by <c>Email:QueueCapacity</c>.
/// <para>
/// <b>There is no outbox table, deliberately, and this is the most important decision in S12.</b> The
/// database never holds a replayable token: <c>SingleUseToken</c> stores only a SHA-256 of each one, and
/// <c>InviteService</c> and <c>PasswordResetService</c> both rely on the raw token leaving the server exactly
/// once. A persisted outbox would store the rendered message, link included — a live credential at rest for
/// as long as the row exists. So mail waits here, in memory.
/// </para>
/// <para>
/// <b>The cost: mail still queued at shutdown is lost.</b> That is accepted, not overlooked. Every message
/// can be reissued — an invite or reset issued again, a forgotten-password request submitted again — and
/// the portal still shows each operator-issued link once on the page. One process is the deployment model
/// (S11), so a per-process queue needs no coordination. Don't "fix" this with a table.
/// </para>
/// <para>
/// <b>Callers never wait.</b> <see cref="TryEnqueue"/> returns false when the queue is full and each caller
/// decides what that means — a page that shows the link anyway says so, and an anonymous form says
/// nothing different at all. A request thread blocked on the relay would turn a slow mail server into a
/// slow portal.
/// </para>
/// <para>
/// With email off there is no channel and nothing drains one (see <c>EmailSetup</c>), so enqueueing is a
/// programming error rather than a full queue: nothing should offer to send mail then.
/// </para>
/// </summary>
public sealed class EmailOutbox
{
    private readonly Channel<OutgoingEmail>? _channel;

    public EmailOutbox(EmailOptions options)
    {
        if (options.Enabled)
        {
            // Wait, not DropOldest/DropNewest: with Wait, TryWrite on a full channel returns false and the
            // caller learns of it. The drop modes accept the write and silently discard a message instead.
            _channel = Channel.CreateBounded<OutgoingEmail>(new BoundedChannelOptions(options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });
        }
    }

    /// <summary>
    /// Queues <paramref name="message"/> without waiting. False means the queue is full and the message was
    /// not taken.
    /// </summary>
    public bool TryEnqueue(OutgoingEmail message) =>
        Queue.Writer.TryWrite(message);

    internal ChannelReader<OutgoingEmail> Reader => Queue.Reader;

    private Channel<OutgoingEmail> Queue =>
        _channel ?? throw new InvalidOperationException(
            "Email is not configured (Email:Enabled is off), so nothing may be queued for delivery.");
}
