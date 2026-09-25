using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Server.Data;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Pages.Portal;

/// <summary>
/// The account home. Scoped by the session's <c>user_id</c> and nothing else — the portal's version of
/// the tenancy rule every JSON route follows with the token's.
/// </summary>
public sealed class IndexModel(WorkLedgerDbContext db, WebOptions web) : PageModel
{
    /// <summary>
    /// Set by the redirect after registering or setting the server up. Only changes wording, so anyone
    /// may put it in the URL.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "welcome")]
    public bool Welcome { get; set; }

    /// <summary>
    /// What to give the desktop app to sync with this server: the public address, since the portal and
    /// the sync API share one origin. The handoff from web to client that onboarding ends with.
    /// </summary>
    public string ServerAddress => web.PublicBaseUrl!;

    public string Email { get; private set; } = string.Empty;
    public string? DisplayName { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var userId = PortalSession.UserId(User);
        var user = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.DisplayName, u.CreatedAt })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            // Session validation refuses a session whose user is gone, so this is a race with a deletion
            // at most. Sign-in is where that user belongs.
            return Redirect(PortalSession.LoginPath);
        }

        Email = user.Email ?? string.Empty;
        DisplayName = user.DisplayName;
        CreatedAt = user.CreatedAt;
        return Page();
    }
}
