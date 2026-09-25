using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Pages.Portal;

/// <summary>
/// Portal sign-in (S11): <see cref="CredentialCheck"/>, then the portal cookie. Because it goes through the
/// same check as <c>POST /auth/login</c>, it has the same rules — one message for every failure, the same
/// per-user lockout (a failure here counts against <c>/auth/login</c> and the other way round), and a
/// refusal of an already-locked account that writes nothing. It is rate-limited under the same per-IP
/// policy as that route, configured in <see cref="PortalSetup"/>.
/// <para>
/// <b>No <c>Device</c> row is created or touched.</b> A browser is not a device; see
/// <see cref="PortalSession"/>. <c>User.LastLoginUtc</c> <i>is</i> updated, by the check itself.
/// </para>
/// <para>
/// <b>A lockout says roughly when, never how many attempts are left.</b> A remaining-attempts count would
/// tell an anonymous caller that the address has an account and how close they are to locking it.
/// </para>
/// <para>
/// <b>The return URL must be local.</b> A sign-in page that redirects anywhere it is told to is a
/// phishing kit: a link to the real login page that lands the victim, freshly signed in and trusting, on
/// a lookalike.
/// </para>
/// </summary>
public sealed class LoginModel(CredentialCheck credentialCheck) : PageModel
{
    [BindProperty]
    public string? Email { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public IActionResult OnGet() =>
        // Already signed in: skip the form rather than invite a second session.
        User.Identity?.IsAuthenticated == true ? RedirectToLocal() : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var result = await credentialCheck.CheckAsync(Email, Password, cancellationToken);
        Password = null;

        switch (result)
        {
            case CredentialCheckResult.Verified { User: var user }:
                await PortalSession.SignInAsync(HttpContext, user);
                return RedirectToLocal();

            case CredentialCheckResult.LockedOut { UntilUtc: var until }:
                var minutes = Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes));
                Error = "Too many failed sign-in attempts. This account is temporarily locked; " +
                    $"try again in about {minutes} minute{(minutes == 1 ? "" : "s")}.";
                return Page();

            default:
                Error = "Invalid email or password.";
                return Page();
        }
    }

    private IActionResult RedirectToLocal() =>
        !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? LocalRedirect(ReturnUrl)
            : Redirect(WebOptions.PortalPath);
}
