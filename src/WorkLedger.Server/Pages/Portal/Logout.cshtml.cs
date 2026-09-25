using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Pages.Portal;

/// <summary>
/// Ends this browser's portal session.
/// <para>
/// <b>POST only, with antiforgery.</b> A GET that signed you out could be fired by an <c>&lt;img&gt;</c>
/// on any page; the GET here only renders the button.
/// </para>
/// <para>
/// <b>It clears the cookie and bumps nothing.</b> Rotating the security stamp would sign every desktop
/// install out as well, and closing a browser tab is not a reason to do that. The portal has a "sign out
/// everywhere" for when that is what is meant.
/// </para>
/// <para>
/// Anonymous, so it still works for a session that has already lapsed.
/// </para>
/// </summary>
public sealed class LogoutModel : PageModel
{
    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(PortalSession.Scheme);
        return Redirect(PortalSession.LoginPath);
    }
}
