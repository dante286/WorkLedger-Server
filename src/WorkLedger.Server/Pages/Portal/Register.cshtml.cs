using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Pages.Portal;

/// <summary>
/// Accepting an invite (S11 item 20): the page an invite link opens, and — while
/// <c>Auth:SignupSecretEnabled</c> is on — the place the signup secret still works. It goes through
/// <see cref="RegistrationService.RegisterAsync"/>, so every rule is S10's: the invite is checked,
/// bound-address and all, and redeemed in the same transaction as the account it creates, so a
/// registration that fails — a taken email included — leaves the invite unspent.
/// <para>
/// <b>Loading this page never spends anything.</b> Chat and mail clients fetch links to build previews,
/// and a GET that redeemed an invite would burn it before the recipient ever clicked. The link carries
/// the token in its fragment (see <see cref="PortalLinks"/>), which never reaches the server; the static
/// script copies it into the visible invite-code field, which also takes a pasted code for when there is
/// no script or the link was mangled in transit. Only the POST redeems.
/// </para>
/// <para>
/// <b>A bad invite gets the server's own sentence</b> (<see cref="RegistrationService.Sentence"/>) — the
/// words <c>/auth/register</c> answers with — because the holder needs to know which thing to go back to
/// the sender about. Only an unknown code is vague, as it is on the JSON route.
/// </para>
/// <para>
/// Success signs the new user straight in and lands them on their account, which shows the server
/// address to give the desktop app: that handoff from web to client is the whole onboarding story now.
/// </para>
/// </summary>
public sealed class RegisterModel(RegistrationService registration, AuthOptions auth) : PageModel
{
    [BindProperty] public string? InviteToken { get; set; }

    [BindProperty] public string? Secret { get; set; }

    [BindProperty] public string? Email { get; set; }

    [BindProperty] public string? DisplayName { get; set; }

    [BindProperty] public string? Password { get; set; }

    [BindProperty] public string? ConfirmPassword { get; set; }

    public bool RegistrationOpen => auth.RegistrationEnabled;

    public bool AcceptsSecret => auth.SignupSecretEnabled;

    public int MinPasswordLength => auth.MinPasswordLength;

    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (RegistrationOpen && !string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            Error = "The two passwords don't match.";
            return Cleared();
        }

        var result = await registration.RegisterAsync(
            new RegisterUserRequest(Email ?? string.Empty, Password ?? string.Empty, DisplayName, InviteToken),
            Secret,
            cancellationToken);

        switch (result)
        {
            case RegistrationResult.Succeeded { User: var user }:
                await PortalSession.SignInAsync(HttpContext, user);
                return Redirect(WebOptions.PortalPath + "?welcome=true");

            case RegistrationResult.Invalid { Error: var error }:
                Error = error;
                break;

            case RegistrationResult.Refused { Reason: RegistrationRefusal.Unauthorized }:
                Error = AcceptsSecret
                    ? "That invite code or signup secret isn't valid. Check the link you were sent, or ask for a new one."
                    : "That invite code isn't valid. Check the link you were sent, or ask whoever invited you for a new one.";
                break;

            case RegistrationResult.Refused { Reason: var reason }:
                Error = RegistrationService.Sentence(reason) ?? "Registration could not be completed.";
                break;
        }

        return Cleared();
    }

    /// <summary>Never echo a password or the secret back into the page.</summary>
    private PageResult Cleared()
    {
        Secret = null;
        Password = null;
        ConfirmPassword = null;
        return Page();
    }
}
