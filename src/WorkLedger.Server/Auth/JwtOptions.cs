using System.Security.Cryptography;
using System.Text;

namespace WorkLedger.Server.Auth;

public sealed record JwtOptions(string SigningKey, string Issuer, string Audience, int ExpiryDays);

public sealed record BootstrapOptions(string Secret)
{
    /// <summary>
    /// Whether <paramref name="provided"/> is the operator's secret. Null — nothing presented — never
    /// matches. The one comparison every secret-gated path makes (<c>/auth/register</c>,
    /// <c>/auth/claim</c>, <c>/admin/bootstrap</c>), so the caller decides only <i>where</i> the value
    /// arrives from (a header for the JSON API) and none of them can grow a weaker compare.
    /// <para>
    /// Constant-time, exactly as S2's <c>/auth/register-device</c> did it: a byte-by-byte early-exit
    /// compare would leak the secret's prefix to a caller willing to time enough requests.
    /// </para>
    /// </summary>
    public bool SecretMatches(string? provided) =>
        provided is not null &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(Secret));
}
