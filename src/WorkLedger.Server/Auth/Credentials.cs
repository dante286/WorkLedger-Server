using WorkLedger.Contracts.Auth;

namespace WorkLedger.Server.Auth;

/// <summary>
/// Validated and normalized signup input. <c>/auth/register</c> and <c>/auth/claim</c> take the same
/// body and enforce the same rules, so the rules live here once.
/// <para>
/// <b>Email validation is deliberately shallow</b>: non-empty, and one <c>@</c> with a non-empty local
/// part and a non-empty domain part. It is not an RFC-5322 regex attempt, because those either reject
/// addresses that genuinely work or accept ones that do not, and the only authority on whether an
/// address is real is a message arriving at it. There is no SMTP here to send one (email verification is
/// deferred), so a shallow syntactic check is the honest limit of what this server can know.
/// </para>
/// <para>
/// <b>Password rules are length-only, and that is the point.</b> Per NIST 800-63B, length is the control
/// that matters; composition requirements push people toward <c>Password1!</c> and a sticky note. The
/// floor is <c>Auth:MinPasswordLength</c>; the ceiling is
/// <see cref="AuthOptions.MaxPasswordLength"/>, which is not policy but a guard against an anonymous
/// caller handing the PBKDF2 hasher a megabyte per request.
/// </para>
/// <para>
/// <b>Whitespace is trimmed from the email and never from the password.</b> A trailing space in an
/// address is a typo; in a passphrase it is a character the user chose.
/// </para>
/// </summary>
internal sealed record Credentials(string Email, string NormalizedEmail, string Password, string? DisplayName)
{
    public static bool TryValidate(
        RegisterUserRequest? request,
        AuthOptions auth,
        out Credentials credentials,
        out string error)
    {
        credentials = null!;

        if (request is null)
        {
            error = "A request body with an email and a password is required.";
            return false;
        }

        var email = request.Email?.Trim() ?? string.Empty;
        if (email.Length == 0)
        {
            error = "Email is required.";
            return false;
        }

        if (!LooksLikeEmail(email))
        {
            error = "Email must contain a single '@' with text on both sides of it.";
            return false;
        }

        if (!TryValidatePassword(request.Password, auth, out error))
        {
            return false;
        }

        var password = request.Password ?? string.Empty;
        var displayName = request.DisplayName?.Trim();
        credentials = new Credentials(
            Email: email,
            // ToUpperInvariant, not the current culture's casing: the container runs
            // invariant-globalization, and a culture-sensitive fold would make uniqueness depend on
            // which machine happened to write the row.
            NormalizedEmail: email.ToUpperInvariant(),
            Password: password,
            DisplayName: string.IsNullOrEmpty(displayName) ? null : displayName);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The email syntax rule on its own, shared so <c>POST /admin/invites</c> can refuse to bind an
    /// invite to an address that <c>/auth/register</c> would then reject — which would produce an invite
    /// nobody can redeem and no error saying why. See this type's doc for why the rule is this shallow.
    /// </summary>
    public static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && at == email.LastIndexOf('@') && at != email.Length - 1;
    }

    /// <summary>
    /// The password policy on its own, because <c>POST /auth/password</c> has to apply exactly the rules
    /// registration applies to a new password while having no email or display name to validate. Sharing
    /// this method rather than the message text is what keeps the two from drifting.
    /// </summary>
    public static bool TryValidatePassword(string? password, AuthOptions auth, out string error)
    {
        password ??= string.Empty;

        if (password.Length < auth.MinPasswordLength)
        {
            error = $"Password must be at least {auth.MinPasswordLength} characters.";
            return false;
        }

        if (password.Length > AuthOptions.MaxPasswordLength)
        {
            error = $"Password must be at most {AuthOptions.MaxPasswordLength} characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
