using Microsoft.AspNetCore.Identity;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Auth;

/// <summary>
/// Password hashing and verification, wrapping <see cref="PasswordHasher{TUser}"/> from
/// <c>Microsoft.Extensions.Identity.Core</c>, which arrives with the ASP.NET Core shared framework and
/// so needs no <c>PackageReference</c> (adding one warns NU1510).
/// <para>
/// <b>Why that package and not ASP.NET Core Identity.</b> What we want from Identity is exactly one
/// thing: a maintained PBKDF2-SHA512 implementation with a version-prefixed hash format and a
/// rehash-needed signal, so an iteration-count bump years from now is a format change we can detect
/// rather than a silent lockout. What we do not want is the rest of it — Identity's own schema and
/// <c>IdentityDbContext</c>, its cookie/SignInManager stack, and its opinions about a custom JWT
/// pipeline that already works. <c>Microsoft.Extensions.Identity.Core</c> carries the hasher without
/// any of that, and <c>AddIdentity</c> is deliberately never called.
/// </para>
/// <para>
/// The Identity type is referenced only here so the rest of the codebase depends on this class instead.
/// The <see cref="User"/> generic argument is a formality: the default hasher never reads the user
/// object, and this method passes <c>null!</c> to say so out loud.
/// </para>
/// </summary>
public sealed class PasswordService
{
    private readonly PasswordHasher<User> _hasher = new();

    /// <summary>
    /// Hashes <paramref name="password"/> with a fresh random salt, so the same password hashed twice
    /// produces two different strings. Safe to store as-is; the format carries its own version marker.
    /// </summary>
    public string Hash(string password) => _hasher.HashPassword(null!, password);

    /// <summary>
    /// Verifies <paramref name="password"/> against a stored hash. <c>RehashNeeded</c> means the hash is
    /// valid but was produced by an older format and should be replaced on the next successful login —
    /// <c>CredentialCheck</c> acts on it, rehashing on the way through.
    /// <para>
    /// A stored value that is not a hash at all — a hand-edited row, a column restored from somewhere it
    /// should not have been — makes the underlying hasher throw while decoding the base64. That is
    /// caught and reported as an ordinary failure, for two reasons: a corrupt hash must not be able to
    /// authenticate anyone, and login (S7) has to answer it with the same uniform 401 as a wrong
    /// password rather than with a 500 that tells the caller this particular row is interesting.
    /// </para>
    /// </summary>
    public PasswordVerification Verify(string passwordHash, string password)
    {
        PasswordVerificationResult result;
        try
        {
            result = _hasher.VerifyHashedPassword(null!, passwordHash, password);
        }
        catch (FormatException)
        {
            return PasswordVerification.Failed;
        }

        return result switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.RehashNeeded,
            _ => PasswordVerification.Failed,
        };
    }
}

public enum PasswordVerification
{
    Failed = 0,
    Success = 1,
    RehashNeeded = 2,
}
