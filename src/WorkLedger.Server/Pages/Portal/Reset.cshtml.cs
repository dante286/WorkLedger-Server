using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Pages.Portal;

/// <summary>
/// Redeeming a password reset (S11 item 21), through <see cref="PasswordResetService.RedeemAsync"/>:
/// the password policy, a stamp bump that signs every device out, cleared lockout counters, and eviction
/// after commit — the same code <c>POST /auth/password/reset</c> runs.
/// <para>
/// <b>Loading this page never spends the token</b>, for the reason <see cref="RegisterModel"/> gives:
/// the link carries it in the fragment, the static script copies it into the visible field, and only the
/// POST redeems.
/// </para>
/// <para>
/// <b>An unknown, expired or spent token all get one sentence</b>
/// (<see cref="PasswordResetService.InvalidTokenMessage"/>), as on the JSON route. A new password that
/// fails the policy is checked before the token is looked at, so it leaves the token spendable — and the
/// page says so, because otherwise the person assumes they have to ask for another link.
/// </para>
/// <para>
/// <b>Success does not sign anyone in.</b> Redeeming proves possession of a link; the next sign-in proves
/// the password. It lands on a confirmation (a redirect, so a refresh cannot re-post a spent token and
/// report a confusing failure) that links to sign-in.
/// </para>
/// </summary>
public sealed class ResetModel(PasswordResetService passwordResets, AuthOptions auth) : PageModel
{
    [BindProperty] public string? Token { get; set; }

    [BindProperty] public string? Password { get; set; }

    [BindProperty] public string? ConfirmPassword { get; set; }

    [BindProperty(SupportsGet = true, Name = "done")]
    public bool Done { get; set; }

    public int MinPasswordLength => auth.MinPasswordLength;

    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Done = false;
        if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            Error = "The two passwords don't match. Your reset link has not been used.";
            return Cleared();
        }

        switch (await passwordResets.RedeemAsync(Token, Password, cancellationToken))
        {
            case PasswordResetRedemption.Redeemed:
                return Redirect(WebOptions.PortalPath + "/reset?done=true");

            case PasswordResetRedemption.MissingToken:
                Error = "Paste the reset code from your link — the part after #token=.";
                break;

            case PasswordResetRedemption.InvalidPassword { Error: var error }:
                Error = $"{error} Your reset link has not been used and still works.";
                break;

            default:
                Error = PasswordResetService.InvalidTokenMessage;
                Token = null;
                break;
        }

        return Cleared();
    }

    private PageResult Cleared()
    {
        Password = null;
        ConfirmPassword = null;
        return Page();
    }
}
