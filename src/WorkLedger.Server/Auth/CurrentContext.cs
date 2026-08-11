using System.Security.Claims;

namespace WorkLedger.Server.Auth;

/// <summary>(AccountId, DeviceId) resolved from the caller's JWT claims — see JwtTokenService.</summary>
public interface ICurrentContext
{
    int AccountId { get; }
    string DeviceId { get; }
}

public sealed class CurrentContext(IHttpContextAccessor httpContextAccessor) : ICurrentContext
{
    public int AccountId => int.Parse(RequireClaim("account_id"));

    public string DeviceId => RequireClaim("device_id");

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
