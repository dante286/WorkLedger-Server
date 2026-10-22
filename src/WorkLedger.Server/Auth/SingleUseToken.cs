using System.Security.Cryptography;

namespace WorkLedger.Server.Auth;

/// <summary>
/// Mints and fingerprints the one-time tokens this API hands out — invites and password resets.
/// Lookup and redemption are not here: each table's service locks its own row <c>FOR UPDATE</c> by this
/// fingerprint (<see cref="InviteService"/>, <see cref="PasswordResetService"/>), because the lock is
/// what makes a token single-use and it has to sit inside that caller's transaction.
/// One implementation, deliberately: two copies of "generate a secret and store a hash of it" is how one
/// of them ends up with fewer bits or a weaker digest, and nothing would notice.
/// <para>
/// <b>These are not passwords and must not be treated as such.</b> A token from here is
/// <see cref="TokenBytes"/> bytes straight out of <see cref="RandomNumberGenerator"/> — no structure, no
/// dictionary, nothing to guess at. So the stored fingerprint is a plain SHA-256 and <b>not</b> PBKDF2:
/// <list type="bullet">
/// <item><description>
/// A work factor exists to make each guess expensive against a <i>guessable</i> secret. There is no
/// guessing strategy against 256 bits of entropy that a slow hash improves on, so it buys nothing.
/// </description></item>
/// <item><description>
/// It costs the lookup. A password hash is salted per row and therefore cannot be indexed — redeeming
/// would mean loading every live token and verifying each one, which is both slower in total and gets
/// worse as the table grows. A deterministic digest is a unique-index hit.
/// </description></item>
/// </list>
/// Do not "harden" this into a password hasher. The property that matters is already there: the database
/// holds no value that can be replayed, because <see cref="Hash"/> is one-way.
/// </para>
/// <para>
/// <b>There is no constant-time comparison here, and none is needed.</b> The only comparison happens
/// inside Postgres as an indexed equality on a hash, so no branch in this process depends on how much of
/// a submitted token was correct. A timing signal about a digest would not help an attacker reach a
/// preimage anyway.
/// </para>
/// </summary>
public static class SingleUseToken
{
    /// <summary>
    /// 32 bytes — 256 bits. Well past anything brute-forceable, and it keeps the rendered token short
    /// enough to paste into a chat message without wrapping, which is how these actually get delivered.
    /// </summary>
    private const int TokenBytes = 32;

    /// <summary>
    /// A fresh token, base64url-encoded so it survives a URL, a shell argument and a copy-paste out of a
    /// terminal without escaping. <b>The caller must return this to the operator exactly once and store
    /// only its <see cref="Hash"/>.</b>
    /// </summary>
    public static string Generate() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>
    /// The stored fingerprint of <paramref name="token"/>: SHA-256, base64url-encoded. Deterministic, so
    /// it can carry a unique index and be looked up in one indexed read.
    /// </summary>
    public static string Hash(string token) =>
        Base64UrlEncode(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Base64 without the characters that need escaping somewhere: <c>+</c> and <c>/</c> become <c>-</c>
    /// and <c>_</c>, and the <c>=</c> padding goes. Both inputs here are fixed-length, so the padding
    /// carries no information and dropping it is not lossy.
    /// </summary>
    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
