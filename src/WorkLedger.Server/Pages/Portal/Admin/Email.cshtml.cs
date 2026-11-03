using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WorkLedger.Server.Email;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Pages.Portal.Admin;

/// <summary>
/// The resolved mail settings and a test message to the signed-in admin (S12 item 10), through
/// <see cref="RelayCheck"/>. <b>This page is how an operator checks their relay</b>; startup deliberately
/// does not contact it.
/// <para>
/// <b>It does not exist while email is off</b>: both handlers answer 404, the same as a page that was never
/// mapped, and the admin navigation does not link to it.
/// </para>
/// <para>
/// <b>The SMTP password is never rendered</b>, not even masked or by length — only whether a username is
/// configured. The relay's reply text to a failed test is shown here, to the admin who sent it, and only
/// here (see <see cref="RelayCheck"/>).
/// </para>
/// </summary>
public sealed class EmailModel(EmailOptions options, RelayCheck relayCheck) : PageModel
{
    public EmailOptions Options => options;

    public RelayCheckResult? Result { get; private set; }

    public IActionResult OnGet() => options.Enabled ? Page() : NotFound();

    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return NotFound();
        }

        Result = await relayCheck.SendTestAsync(PortalSession.UserId(User), cancellationToken);
        return Page();
    }
}
