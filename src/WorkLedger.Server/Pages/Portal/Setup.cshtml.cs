using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Pages.Portal;

/// <summary>
/// First run (S11 item 19): one page that knows how far this server is through being set up
/// (<see cref="ServerSetup"/>) and offers the single step that state allows. It replaces registering
/// the first account from the desktop client.
/// <list type="bullet">
/// <item><description><b>No users:</b> create the first account, which is the administrator
/// (<see cref="RegistrationService.RegisterFirstUserAsync"/>).</description></item>
/// <item><description><b>The pre-S6 legacy row:</b> claim it, keeping all its data
/// (<see cref="RegistrationService.ClaimAsync"/>), and make it the administrator in the same step —
/// on a server whose only account is being claimed by the person holding the secret, a separate
/// promotion would only ask them for the secret twice.</description></item>
/// <item><description><b>Users but no admin:</b> sign in and promote yourself
/// (<see cref="CredentialCheck"/>, then <see cref="AdminOperations.BootstrapAsync"/>).</description></item>
/// <item><description><b>An admin exists:</b> <c>404</c>, so the page does not advertise a secret
/// prompt on a server that has no use for one.</description></item>
/// </list>
/// <para>
/// <b>Every step requires <c>Bootstrap:Secret</c></b>, because otherwise the first person to find a
/// fresh server would own it. A wrong secret is said to be wrong: the page is rate-limited, and the
/// secret is the operator's own, so there is no account to protect by being vague. The secret arrives in
/// the POST body and the request log records paths only.
/// </para>
/// </summary>
public sealed class SetupModel(
    WorkLedgerDbContext db,
    RegistrationService registration,
    CredentialCheck credentialCheck,
    AdminOperations admin,
    BootstrapOptions bootstrap) : PageModel
{
    private const string WrongSecret =
        "That isn't this server's setup secret. It is the Bootstrap__Secret value the server was started with.";

    public ServerSetupState State { get; private set; }

    [BindProperty] public string? Secret { get; set; }

    [BindProperty] public string? Email { get; set; }

    [BindProperty] public string? DisplayName { get; set; }

    [BindProperty] public string? Password { get; set; }

    [BindProperty] public string? ConfirmPassword { get; set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        State = await ServerSetup.ResolveAsync(db, cancellationToken);
        return State is ServerSetupState.Ready ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        State = await ServerSetup.ResolveAsync(db, cancellationToken);
        var result = State switch
        {
            ServerSetupState.Empty or ServerSetupState.Unclaimed => await CreateOrClaimAsync(cancellationToken),
            ServerSetupState.NoAdmin => await PromoteAsync(cancellationToken),
            _ => NotFound(),
        };

        Secret = null;
        Password = null;
        ConfirmPassword = null;
        return result;
    }

    private async Task<IActionResult> CreateOrClaimAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            Error = "The two passwords don't match.";
            return Page();
        }

        var request = new RegisterUserRequest(Email ?? string.Empty, Password ?? string.Empty, DisplayName);
        var result = State is ServerSetupState.Empty
            ? await registration.RegisterFirstUserAsync(request, Secret, cancellationToken)
            : await registration.ClaimAsync(request, Secret, cancellationToken);

        switch (result)
        {
            case RegistrationResult.Succeeded { User: var user }:
                if (State is ServerSetupState.Unclaimed)
                {
                    // The secret has just been checked by the claim, and the claimed row is the only user,
                    // so this cannot find an existing admin — unless one appeared in between, in which
                    // case the claim still stands and the new admin can promote them.
                    await admin.BootstrapAsync(user.Id, Secret, cancellationToken);
                }

                return await SignInAsync(user);

            case RegistrationResult.Invalid { Error: var error }:
                Error = error;
                return Page();

            case RegistrationResult.Refused { Reason: RegistrationRefusal.Unauthorized }:
                Error = WrongSecret;
                return Page();

            default:
                // AlreadySetUp / NothingToClaim: someone else finished this step first. Show whatever the
                // server needs now instead.
                return Redirect(WebOptions.PortalPath + "/setup");
        }
    }

    private async Task<IActionResult> PromoteAsync(CancellationToken cancellationToken)
    {
        // Checked before the password, so a wrong secret spends nothing from the account's sign-in budget.
        if (!bootstrap.SecretMatches(Secret))
        {
            Error = WrongSecret;
            return Page();
        }

        switch (await credentialCheck.CheckAsync(Email, Password, cancellationToken))
        {
            case CredentialCheckResult.Verified { User: var user }:
                if (await admin.BootstrapAsync(user.Id, Secret, cancellationToken) is not AdminOutcome.Done)
                {
                    Error = "An administrator was set up while you were on this page. Sign in normally instead.";
                    return Page();
                }

                return await SignInAsync(user);

            case CredentialCheckResult.LockedOut:
                Error = "Too many failed sign-in attempts. This account is temporarily locked; try again later.";
                return Page();

            default:
                Error = "Invalid email or password.";
                return Page();
        }
    }

    private async Task<IActionResult> SignInAsync(User user)
    {
        await PortalSession.SignInAsync(HttpContext, user);
        return Redirect(WebOptions.PortalPath + "?welcome=true");
    }
}
