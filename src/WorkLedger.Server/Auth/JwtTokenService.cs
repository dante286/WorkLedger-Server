using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace WorkLedger.Server.Auth;

public sealed class JwtTokenService(JwtOptions options)
{
    public (string Token, DateTime ExpiresAtUtc) IssueToken(int userId, int deviceRowId, string deviceId)
    {
        var now = DateTime.UtcNow;
        var expiresAtUtc = now.AddDays(options.ExpiryDays);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, deviceRowId.ToString()),
            new Claim("user_id", userId.ToString()),
            new Claim("device_id", deviceId),
        };

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }
}
