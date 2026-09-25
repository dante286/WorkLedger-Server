using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Pages.Portal.Account;

/// <summary>
/// Deleting your own account (S11 item 15), through <see cref="AccountSelfService.DeleteAccountAsync"/>:
/// the same two independent gates as <c>DELETE /auth/account</c> — the current password, and the
/// account's email typed out — then the one deletion pipeline, <see cref="AccountDeletion"/>.
/// <para>
/// <b>The last administrator may delete their account</b>, exactly as on the JSON route; the service
/// explains why guarding it would be a bug. The page warns them first, and says what happens next: with
/// no admin left, <c>/portal/setup</c> starts offering the promote-yourself step again.
/// </para>
/// <para>
/// On success the session is signed out and the browser sent to a static confirmation. Desktop installs
/// signed in to the account find out on their next sync, as a <c>401</c>.
/// </para>
/// </summary>
public sealed class DeleteModel(AccountSelfService selfService, WorkLedgerDbContext db) : PageModel
{
    public string Email { get; private set; } = string.Empty;

    /// <summary>Whether this account is the only administrator who can sign in, which earns a warning.</summary>
    public bool IsLastAdmin { get; private set; }

    public string? Error { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(string? password, string? confirmEmail, CancellationToken cancellationToken)
    {
        switch (await selfService.DeleteAccountAsync(PortalSession.UserId(User), password, confirmEmail, cancellationToken))
        {
            case SelfServiceOutcome.Done:
                await HttpContext.SignOutAsync(PortalSession.Scheme);
                return Redirect(WebOptions.PortalPath + "/deleted");
            case SelfServiceOutcome.WrongPassword:
                Error = "Your password is incorrect. Nothing was deleted.";
                break;
            case SelfServiceOutcome.ConfirmationMismatch:
                Error = "The email you typed doesn't match your account. Nothing was deleted.";
                break;
            case SelfServiceOutcome.NoPassword:
                Error = "This account has no password yet, so it can't be confirmed for deletion.";
                break;
            default:
                return Redirect(PortalSession.LoginPath);
        }

        return await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        var userId = PortalSession.UserId(User);
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.IsAdmin })
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return Redirect(PortalSession.LoginPath);
        }

        Email = user.Email ?? string.Empty;
        IsLastAdmin = user.IsAdmin &&
            !await db.Users.AnyAsync(u => u.Id != userId && u.IsAdmin && u.DisabledAtUtc == null, cancellationToken);
        return Page();
    }
}
