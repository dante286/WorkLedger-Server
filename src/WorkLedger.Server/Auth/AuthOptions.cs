using System.Globalization;

namespace WorkLedger.Server.Auth;

/// <summary>
/// The account-policy knobs an operator can turn, resolved in one place so <c>/auth/register</c>,
/// <c>/auth/claim</c> and <c>/auth/capabilities</c> cannot disagree about what this server's rules are.
/// <para>
/// <b>One accessor for the lockout numbers.</b> S6 reads <see cref="MaxFailedLogins"/> and
/// <see cref="LockoutMinutes"/> only to report them on <c>/auth/capabilities</c> — nothing enforces them
/// until S7 adds login. That is deliberate: it lets <c>AuthCapabilitiesResponse</c> settle its shape in
/// one plan, so the client vendors the contract once. S7's enforcement reads these same properties
/// rather than re-reading configuration, which is what stops the advertised policy and the enforced one
/// from drifting apart.
/// </para>
/// <para>
/// A <b>blank</b> value counts as unset throughout, for the same reason <c>PostgresConnection</c> treats
/// it that way: compose renders an unset optional override as <c>""</c>, and a plain null-coalesce would
/// take that empty string as a configured answer.
/// </para>
/// </summary>
public sealed record AuthOptions(
    bool RegistrationEnabled,
    int MinPasswordLength,
    int MaxFailedLogins,
    int LockoutMinutes)
{
    /// <summary>
    /// The ceiling on a submitted password, well above any real passphrase. Its job is not policy but
    /// to stop an anonymous caller handing the PBKDF2 hasher a megabyte of input per request.
    /// </summary>
    public const int MaxPasswordLength = 256;

    public const int DefaultMinPasswordLength = 12;
    public const int DefaultMaxFailedLogins = 5;
    public const int DefaultLockoutMinutes = 15;

    /// <summary>
    /// The flat alias for <c>Auth:RegistrationEnabled</c>. Operators reach for this name, so it is
    /// accepted verbatim; the colon-keyed setting is canonical and wins when both are present.
    /// </summary>
    public const string RegistrationEnabledAlias = "REGISTRATIONS_ALLOWED";

    public static AuthOptions Resolve(IConfiguration configuration) => new(
        RegistrationEnabled: Flag(configuration, "Auth:RegistrationEnabled", RegistrationEnabledAlias, @default: true),
        MinPasswordLength: Number(configuration, "Auth:MinPasswordLength", DefaultMinPasswordLength),
        MaxFailedLogins: Number(configuration, "Auth:MaxFailedLogins", DefaultMaxFailedLogins),
        LockoutMinutes: Number(configuration, "Auth:LockoutMinutes", DefaultLockoutMinutes));

    private static bool Flag(IConfiguration configuration, string key, string alias, bool @default)
    {
        var value = Configured(configuration, key) ?? Configured(configuration, alias);
        return value is null || !bool.TryParse(value, out var parsed) ? @default : parsed;
    }

    private static int Number(IConfiguration configuration, string key, int @default)
    {
        var value = Configured(configuration, key);
        return value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : @default;
    }

    private static string? Configured(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
