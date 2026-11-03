using System.Text;

namespace WorkLedger.Server.Email;

/// <summary>What a message is for. The only description of a message that ever reaches a log line.</summary>
public enum EmailKind
{
    Invite,
    AdminPasswordReset,
    SelfServicePasswordReset,
    PasswordChanged,
    Test,
}

/// <summary>A subject and a plain-text body, as <see cref="EmailMessages"/> renders them.</summary>
public sealed record EmailContent(string Subject, string Body);

/// <summary>
/// One item of work in <see cref="EmailOutbox"/>: either a message ready to send (<see cref="OutgoingEmail"/>)
/// or a request whose message <see cref="EmailDeliveryService"/> has yet to decide on
/// (<see cref="ForgotPasswordRequest"/>). Like everything in the queue it lives in memory only, and is never
/// serialized, logged or persisted.
/// </summary>
public abstract record EmailWork;

/// <summary>
/// Someone asked at <c>/portal/forgot</c> for a reset link to be sent to <see cref="NormalizedEmail"/> (S12).
/// <para>
/// <b>This is all the request does</b>: the page puts one of these on the queue and answers, the same way
/// for every address, without looking anything up. Whether that address has an account, whether it may
/// have a reset, and the reset itself are decided later, off the request path, by <c>SelfServiceRecovery</c>
/// — so neither the page's content nor its timing says whether the account exists.
/// </para>
/// <para>
/// The address is the caller's input, not anything this server holds: its <see cref="object.ToString"/>
/// prints nothing of it.
/// </para>
/// </summary>
public sealed record ForgotPasswordRequest(string NormalizedEmail) : EmailWork
{
    protected override bool PrintMembers(StringBuilder builder) => false;
}

/// <summary>
/// One message on its way to the relay.
/// <para>
/// <b>It may hold a live token</b> — an invite or reset link, and the same token again on its own line —
/// <b>and that is acceptable only because it lives in memory</b>, in <see cref="EmailOutbox"/>'s channel,
/// for as long as delivery takes. It must never be serialized, logged or persisted: the database holds
/// only a SHA-256 of each token (<c>SingleUseToken</c>), and anything that wrote one of these down would
/// put a replayable credential at rest. That is why there is no outbox table (see <see cref="EmailOutbox"/>).
/// </para>
/// <para>
/// Its <see cref="object.ToString"/> prints <see cref="Kind"/> and <see cref="UserId"/> only, so one that
/// slips into an interpolated log line takes neither the recipient's address nor the body with it.
/// </para>
/// </summary>
/// <param name="Kind">What the message is for; logged.</param>
/// <param name="UserId">
/// The account the message concerns, when there is one — null for an invite, whose recipient has no account
/// yet, and for the test message. Logged in place of the address.
/// </param>
/// <param name="To">The recipient's address. <b>Never logged.</b></param>
/// <param name="Content">The rendered message. <b>Never logged</b>: it may contain a token.</param>
public sealed record OutgoingEmail(EmailKind Kind, int? UserId, string To, EmailContent Content) : EmailWork
{
    protected override bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Kind = ").Append(Kind).Append(", UserId = ").Append(UserId?.ToString() ?? "-");
        return true;
    }
}
