using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Pages.Portal.Account;

/// <summary>
/// Change password and sign out everywhere (S11 item 14), through <see cref="AccountSelfService"/> — the
/// same stamp bump, lockout reset and eviction as <c>POST /auth/password</c> and
/// <c>POST /auth/logout-all</c>, so <b>every desktop install is signed out</b>, and the page says so before
/// either button is pressed.
/// <para>
/// <b>This browser stays signed in.</b> The stamp bump ends every session, this one included, so after
/// the commit the cookie is re-issued with the new stamp (<see cref="PortalSession.SignInAsync"/>) —
/// otherwise your own next click would bounce you to sign-in for the thing you just did. It is the
/// portal's version of the JSON routes handing the calling device a fresh token.
/// </para>
/// <para>
/// <b>A wrong current password is a message, not a sign-out</b>, and it does not count towards the
/// sign-in lockout — the same as <c>POST /auth/password</c>. The page is rate-limited instead
/// (see <see cref="PortalSetup"/>), so a stolen session cannot guess at the password through it.
/// </para>
/// </summary>
public sealed class SecurityModel(AccountSelfService selfService, AuthOptions auth) : PageModel
{
    public int MinPasswordLength => auth.MinPasswordLength;

    public string? Message { get; private set; }

    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostPasswordAsync(
        string? currentPassword, string? newPassword, string? confirmPassword, CancellationToken cancellationToken)
    {
        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            Error = "The two new passwords don't match. Nothing was changed.";
            return Page();
        }

        var result = await selfService.ChangePasswordAsync(
            PortalSession.UserId(User), currentPassword, newPassword, cancellationToken);

        switch (result.Outcome)
        {
            case SelfServiceOutcome.Done:
                await PortalSession.SignInAsync(HttpContext, result.User!);
                Message = "Your password has been changed. Every desktop install has been signed out — sign in " +
                    "on each with the new password. This browser is still signed in.";
                break;
            case SelfServiceOutcome.WrongPassword:
                Error = "Your current password is incorrect. Nothing was changed.";
                break;
            case SelfServiceOutcome.InvalidPassword:
                Error = $"{result.Error} Nothing was changed.";
                break;
            case SelfServiceOutcome.NoPassword:
                Error = "This account has no password yet.";
                break;
            default:
                return Redirect(PortalSession.LoginPath);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSignOutEverywhereAsync(CancellationToken cancellationToken)
    {
        var result = await selfService.SignOutEverywhereAsync(PortalSession.UserId(User), cancellationToken);
        if (result.User is not { } user)
        {
            return Redirect(PortalSession.LoginPath);
        }

        await PortalSession.SignInAsync(HttpContext, user);
        Message = "Every desktop install has been signed out. Each one needs to sign in again. " +
            "This browser is still signed in.";
        return Page();
    }
}
