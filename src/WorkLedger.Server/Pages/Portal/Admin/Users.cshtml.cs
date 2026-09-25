using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Pages.Portal.Admin;

/// <summary>
/// Every user on the server, with the operator's actions on each row (S11 item 16). The page is the
/// portal twin of <c>/admin/users</c> and its action routes, and <b>every action goes through
/// <see cref="AdminOperations"/></b> — so the self-action guard, the last-admin guard, the eviction that
/// makes a suspension or demotion bite on the next request, and the audit line are the code the JSON
/// routes run, not a copy of it. A refusal shows the same reason the JSON route's <c>409</c> gives, in the
/// portal's words.
/// <para>
/// <b>Actions answer with the page, not a redirect.</b> Post-redirect-get would need the outcome carried
/// across the redirect in TempData — a cookie — and for a password reset that outcome is a live token,
/// which must not be written into a cookie for any length of time. Rendering the result directly keeps
/// the link in exactly one response, marked <c>no-store</c>. Re-posting on refresh is harmless: every
/// action is idempotent or refuses on the second go.
/// </para>
/// <para>
/// <b>Demoting yourself is allowed</b> (S10), and then this page sends you to your account instead of
/// re-rendering: the admin pages refuse your very next request, which is the policy working.
/// </para>
/// </summary>
public sealed class UsersModel(
    AdminOperations admin,
    WorkLedgerDbContext db,
    PortalLinks links,
    AuthOptions auth) : PageModel
{
    public IReadOnlyList<AdminUserSummary> Users { get; private set; } = [];

    public int CurrentUserId { get; private set; }

    /// <summary>What the last action did, when it did something.</summary>
    public string? Message { get; private set; }

    /// <summary>Why the last action was refused, when it was.</summary>
    public string? Error { get; private set; }

    /// <summary>The reset link just issued — shown in this response and never again.</summary>
    public string? ResetLink { get; private set; }

    public string? ResetFor { get; private set; }

    public DateTime ResetExpiresAtUtc { get; private set; }

    public int ResetExpiryHours => auth.PasswordResetExpiryHours;

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    public Task<IActionResult> OnPostUnlockAsync(int userId, CancellationToken cancellationToken) =>
        ActAsync(userId, "unlock", (actor, ct) => admin.UnlockAsync(actor, userId, ct),
            email => $"Unlocked {email}. They can sign in again straight away.", cancellationToken);

    public Task<IActionResult> OnPostDisableAsync(int userId, CancellationToken cancellationToken) =>
        ActAsync(userId, "suspend", (actor, ct) => admin.DisableAsync(actor, userId, ct),
            email => $"Suspended {email}. Every device and browser they were signed in on is signed out; " +
                "their account and data are kept, and restoring them brings it all back.",
            cancellationToken);

    public Task<IActionResult> OnPostEnableAsync(int userId, CancellationToken cancellationToken) =>
        ActAsync(userId, "restore", (actor, ct) => admin.EnableAsync(actor, userId, ct),
            email => $"Restored {email}.", cancellationToken);

    public Task<IActionResult> OnPostPromoteAsync(int userId, CancellationToken cancellationToken) =>
        ActAsync(userId, "promote", (actor, ct) => admin.PromoteAsync(actor, userId, ct),
            email => $"{email} is now an administrator.", cancellationToken);

    public async Task<IActionResult> OnPostDemoteAsync(int userId, CancellationToken cancellationToken)
    {
        var actor = PortalSession.UserId(User);
        var email = await EmailOfAsync(userId, cancellationToken);
        var outcome = await admin.DemoteAsync(actor, userId, cancellationToken);

        // Standing down: the admin pages refuse this session's next request, so go where it is welcome.
        if (outcome is AdminOutcome.Done && userId == actor)
        {
            return Redirect(WebOptions.PortalPath);
        }

        return await ShowAsync(outcome, "demote", email, $"{email} is no longer an administrator.", cancellationToken);
    }

    public Task<IActionResult> OnPostDeleteAsync(int userId, string? confirmEmail, CancellationToken cancellationToken) =>
        // Never null from the form: a blank field is a confirmation that matches nothing, not a missing one.
        ActAsync(userId, "delete", (actor, ct) => admin.DeleteUserAsync(actor, userId, confirmEmail ?? string.Empty, ct),
            email => $"Deleted {email} and all of their synced data. This cannot be undone.", cancellationToken);

    public async Task<IActionResult> OnPostResetAsync(int userId, CancellationToken cancellationToken)
    {
        var email = await EmailOfAsync(userId, cancellationToken);
        var (outcome, reset) = await admin.IssuePasswordResetAsync(
            PortalSession.UserId(User), userId, cancellationToken);

        if (outcome is AdminOutcome.Done)
        {
            ResetLink = links.PasswordReset(reset!.Token);
            ResetFor = email;
            ResetExpiresAtUtc = reset.ExpiresAtUtc;
        }
        else
        {
            Error = Describe(outcome, "reset", email);
        }

        return await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> ActAsync(
        int userId,
        string verb,
        Func<int, CancellationToken, Task<AdminOutcome>> action,
        Func<string, string> success,
        CancellationToken cancellationToken)
    {
        // Read first: after a delete there is no row left to name.
        var email = await EmailOfAsync(userId, cancellationToken);
        var outcome = await action(PortalSession.UserId(User), cancellationToken);
        return await ShowAsync(outcome, verb, email, success(email), cancellationToken);
    }

    private Task<IActionResult> ShowAsync(
        AdminOutcome outcome, string verb, string email, string success, CancellationToken cancellationToken)
    {
        if (outcome is AdminOutcome.Done)
        {
            Message = success;
        }
        else
        {
            Error = Describe(outcome, verb, email);
        }

        return RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        CurrentUserId = PortalSession.UserId(User);
        Users = await admin.ListUsersAsync(cancellationToken);
        return Page();
    }

    /// <summary>
    /// The reason behind each refusal — the same reason the JSON route's body gives, worded for someone
    /// looking at this page rather than writing a script.
    /// </summary>
    private static string Describe(AdminOutcome outcome, string verb, string email) => outcome switch
    {
        AdminOutcome.NoSuchUser => "That user no longer exists.",
        AdminOutcome.SelfAction when verb == "delete" =>
            "You can't delete your own account from the operator pages. Delete it from your own account " +
            "settings, which ask for your password first.",
        AdminOutcome.SelfAction =>
            $"You can't {verb} your own account from the operator pages — they act on other users.",
        AdminOutcome.LastAdmin =>
            $"You can't {verb} the last administrator: this server would be left with nobody able to " +
            "administer it. Promote another user first.",
        AdminOutcome.ConfirmationMissing or AdminOutcome.ConfirmationMismatch =>
            $"The email you typed doesn't match {email}. Nothing was deleted.",
        AdminOutcome.NoCredentials =>
            "That account has no credentials yet, so there is nothing to reset. It is the account from " +
            "before user accounts existed, and it has to be claimed first.",
        _ => "That action could not be completed.",
    };

    private async Task<string> EmailOfAsync(int userId, CancellationToken cancellationToken)
    {
        var email = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.Email)
            .SingleOrDefaultAsync(cancellationToken);
        return string.IsNullOrEmpty(email) ? $"user #{userId}" : email;
    }
}
