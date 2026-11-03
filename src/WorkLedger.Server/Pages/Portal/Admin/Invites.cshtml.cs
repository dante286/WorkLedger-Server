using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Email;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Pages.Portal.Admin;

/// <summary>
/// Issue, list and revoke invites (S11 item 17), through <see cref="InviteService"/> — the same code, the
/// same validation and the same audit line as <c>/admin/invites</c>.
/// <para>
/// <b>A new invite's link is shown exactly once</b>, in the response to the create action and nowhere
/// else: only the token's hash is stored, so nothing can produce it again. That is also why the create
/// action answers with the page rather than redirecting — see <see cref="UsersModel"/> for why a
/// one-time secret must not ride a TempData cookie across a redirect.
/// </para>
/// <para>
/// <b>"Email this invite" (S12)</b> is a checkbox shown only while email is on. Hiding it is not the check:
/// <see cref="InviteService"/> refuses a forged post with email off, and an emailed invite with no bound
/// address, before anything is created. The link is still shown once when it has been emailed.
/// </para>
/// </summary>
public sealed class InvitesModel(
    InviteService invites,
    WorkLedgerDbContext db,
    PortalLinks links,
    AuthOptions auth,
    EmailOptions email) : PageModel
{
    public bool EmailEnabled => email.Enabled;

    public IReadOnlyList<InviteSummary> Invites { get; private set; } = [];

    /// <summary>Email by user id, for the "issued by" and "used by" columns. Ids are what invites store.</summary>
    public IReadOnlyDictionary<int, string> Emails { get; private set; } = new Dictionary<int, string>();

    public int DefaultExpiryDays => auth.InviteExpiryDays;

    public string? Message { get; private set; }

    public string? Error { get; private set; }

    /// <summary>The invite just created — its link is shown in this response and never again.</summary>
    public string? CreatedLink { get; private set; }

    public InviteSummary? Created { get; private set; }

    /// <summary>What happened to the emailed copy, when one was asked for.</summary>
    public string? EmailNote { get; private set; }

    /// <summary>Whether <see cref="EmailNote"/> reports a queued message, rather than a full queue.</summary>
    public bool EmailQueued { get; private set; }

    /// <summary>
    /// The address the new invite is for. A bound property rather than a handler argument, as is
    /// <see cref="InviteNote"/>, because MVC's Trace-level "executing handler … with arguments" line would
    /// otherwise print it.
    /// </summary>
    [BindProperty(Name = "email")]
    public string? InviteEmail { get; set; }

    /// <summary>The operator's free-text note, which may well name the invitee; bound for the reason on <see cref="InviteEmail"/>.</summary>
    [BindProperty(Name = "note")]
    public string? InviteNote { get; set; }

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(
        int? expiresInDays, bool sendEmail, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            // Only a non-number in the expiry field gets here; the service would otherwise quietly take
            // its absence as "the default", which is not what someone who typed something meant.
            Error = "Expires in must be a whole number of days.";
            return await RenderAsync(cancellationToken);
        }

        var result = await invites.IssueAsync(
            PortalSession.UserId(User), InviteEmail, expiresInDays, InviteNote, sendEmail, cancellationToken);

        switch (result)
        {
            case InviteIssue.Issued { Token: var token, Invite: var invite, EmailQueued: var queued }:
                CreatedLink = links.Invite(token);
                Created = invite;
                if (queued is { } wasQueued)
                {
                    EmailQueued = wasQueued;
                    EmailNote = wasQueued
                        ? $"Queued for delivery to {InviteEmail!.Trim()}."
                        : "The mail queue is full, so this invite wasn't emailed; copy the link below instead.";
                }

                break;
            case InviteIssue.InvalidEmail:
                Error = "That email address doesn't look right — it needs a single '@' with text on both sides.";
                break;
            case InviteIssue.EmailNeedsAddress:
                Error = "To email an invite, enter the address it's for: an invite that isn't bound to an " +
                    "address has nobody to send it to. Nothing was created.";
                break;
            case InviteIssue.EmailNotConfigured:
                Error = "Email isn't configured on this server, so the invite wasn't created. Create it " +
                    "without emailing it and pass the link on yourself.";
                break;
            default:
                Error = "Expires in must be at least one day.";
                break;
        }

        return await RenderAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostRevokeAsync(int inviteId, CancellationToken cancellationToken)
    {
        switch (await invites.RevokeAsync(PortalSession.UserId(User), inviteId, cancellationToken))
        {
            case InviteRevocation.Revoked:
                Message = "Invite revoked. Its link no longer works.";
                break;
            case InviteRevocation.AlreadyRedeemed:
                // The same reason the JSON route's 409 gives.
                Error = "That invite has already been used, so there is nothing to revoke — and its record " +
                    "is kept on purpose. To remove the account it created, delete that user instead.";
                break;
            default:
                Error = "That invite no longer exists.";
                break;
        }

        return await RenderAsync(cancellationToken);
    }

    public static string StatusOf(InviteSummary invite) =>
        invite.RedeemedAtUtc is not null ? "Used"
        : invite.ExpiresAtUtc <= DateTime.UtcNow ? "Expired"
        : "Open";

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        Invites = await invites.ListAsync(cancellationToken);

        var ids = Invites
            .SelectMany(i => new[] { i.CreatedByUserId, i.RedeemedByUserId })
            .OfType<int>()
            .Distinct()
            .ToList();
        Emails = await db.Users
            .Where(u => ids.Contains(u.Id) && u.Email != null)
            .ToDictionaryAsync(u => u.Id, u => u.Email!, cancellationToken);

        return Page();
    }
}
