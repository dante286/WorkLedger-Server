using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Pages.Portal.Account;

/// <summary>
/// The desktop installs signed in to this account (S11 item 13), through
/// <see cref="AccountSelfService"/> — the same list and the same removal <c>/auth/devices</c> gives.
/// <para>
/// <b>There is no "this device" marker</b>, unlike the JSON list: a browser is not a device, so no row
/// here is the one you are looking from.
/// </para>
/// <para>
/// <b>Removing a device signs that install out and deletes nothing else</b> — projects, tasks and time
/// entries belong to the account, not to the install that created them (see
/// <see cref="AccountSelfService.RemoveDeviceAsync"/>). The page says so, because "remove device" reads
/// to most people as "delete what's on it".
/// </para>
/// </summary>
public sealed class DevicesModel(AccountSelfService selfService) : PageModel
{
    public IReadOnlyList<DeviceSummary> Devices { get; private set; } = [];

    public string? Message { get; private set; }

    public string? Error { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    public async Task<IActionResult> OnPostRemoveAsync(string? deviceId, string? label, CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(label) ? "That device" : label;
        if (!string.IsNullOrEmpty(deviceId) &&
            await selfService.RemoveDeviceAsync(PortalSession.UserId(User), deviceId, cancellationToken))
        {
            Message = $"{name} has been signed out and removed. Your synced time is untouched; signing in on " +
                "it again adds it back.";
        }
        else
        {
            Error = "That device is no longer on your account.";
        }

        return await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        // No current device to mark: this request came from a browser, not an install.
        Devices = await selfService.ListDevicesAsync(PortalSession.UserId(User), currentDeviceId: null, cancellationToken);
        return Page();
    }
}
