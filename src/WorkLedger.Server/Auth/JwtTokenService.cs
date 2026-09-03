using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace WorkLedger.Server.Auth;

/// <summary>
/// Mints the one kind of token this API issues: long-lived (<c>Jwt:ExpiryDays</c>, default 365) and
/// scoped to a <c>(user, device)</c> pair.
/// <para>
/// <b>Why there are no refresh tokens, and why the access token is not short.</b> This was considered
/// and deferred deliberately, not overlooked. The only client is an offline-first desktop app that may
/// go weeks between syncs, so a 15-minute access token would spend most of its life expired; and it
/// would buy nothing anyway, because the refresh token sitting next to it in the client's
/// <c>AppSettings</c> would be the real credential — the same secret at rest in the same place, with
/// rotation, replay detection and a revocation store added on top. What a long-lived token genuinely
/// needs is a way to be <i>killed</i>, and that is the <c>stamp</c> claim below plus
/// <see cref="TokenRevocation"/>: revocation is the substitute for short expiry here, and it is the
/// mechanism S8's device removal and account deletion pull on. Revisit this only if a browser client
/// appears, where token storage is genuinely worse and the tradeoff changes.
/// </para>
/// </summary>
public sealed class JwtTokenService(JwtOptions options)
{
    /// <param name="securityStamp">
    /// The user's <c>SecurityStamp</c> at the moment of minting. <see cref="TokenRevocation"/> compares
    /// it against the stored column on every authenticated request, so a token minted from a stale value
    /// is dead on arrival — pass the value that is actually in the database, after any bump.
    /// </param>
    public (string Token, DateTime ExpiresAtUtc) IssueToken(
        int userId,
        int deviceRowId,
        string deviceId,
        string securityStamp)
    {
        var now = DateTime.UtcNow;
        var expiresAtUtc = now.AddDays(options.ExpiryDays);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, deviceRowId.ToString()),
            new Claim("user_id", userId.ToString()),
            new Claim("device_id", deviceId),
            new Claim(TokenRevocation.StampClaim, securityStamp),
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
