using System.Security.Claims;

namespace WorkLedger.Server.Auth;

/// <summary>(UserId, DeviceRowId, DeviceId) resolved from the caller's JWT claims — see JwtTokenService.</summary>
public interface ICurrentContext
{
    int UserId { get; }

    /// <summary>
    /// The <c>Devices</c> row's primary key, from the <c>sub</c> claim. Needed by anything that has to
    /// mint a replacement token for the calling device (<c>POST /auth/password</c>) or address the
    /// device's own row; sync and reports scope on <see cref="UserId"/> alone.
    /// </summary>
    int DeviceRowId { get; }

    string DeviceId { get; }
}

public sealed class CurrentContext(IHttpContextAccessor httpContextAccessor) : ICurrentContext
{
    public int UserId => int.Parse(RequireClaim("user_id"));

    public int DeviceRowId =>
        TokenRevocation.TryReadDeviceRowId(Principal, out var deviceRowId)
            ? deviceRowId
            : throw new InvalidOperationException("No authenticated user with a device row id on the current request.");

    public string DeviceId => RequireClaim("device_id");

    private ClaimsPrincipal Principal =>
        httpContextAccessor.HttpContext?.User
        ?? throw new InvalidOperationException("No authenticated user on the current request.");

    private string RequireClaim(string claimType)
    {
        var value = httpContextAccessor.HttpContext?.User.FindFirstValue(claimType);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"No authenticated user with claim '{claimType}' on the current request.");
        }

        return value;
    }
}
