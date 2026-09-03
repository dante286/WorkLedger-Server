using System.Globalization;

namespace WorkLedger.Server.Auth;

/// <summary>
/// The account-policy knobs an operator can turn, resolved in one place so <c>/auth/login</c>,
/// <c>/auth/register</c>, <c>/auth/claim</c>, <c>/auth/password</c> and <c>/auth/capabilities</c> cannot
/// disagree about what this server's rules are.
/// <para>
/// <b>One accessor for the lockout numbers.</b> S6 read <see cref="MaxFailedLogins"/> and
/// <see cref="LockoutMinutes"/> only to report them on <c>/auth/capabilities</c>; S7's login enforces
/// those same properties rather than re-reading configuration, which is what stops the advertised policy
/// and the enforced one from drifting apart. A server that advertises 5/15 and enforces something else is
/// worse than one that advertises nothing at all.
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
    double LockoutMinutes,
    int StampCacheSeconds,
    int LoginRateLimitPerMinute,
    double LoginRateLimitWindowSeconds)
{
    /// <summary>
    /// The ceiling on a submitted password, well above any real passphrase. Its job is not policy but
    /// to stop an anonymous caller handing the PBKDF2 hasher a megabyte of input per request.
    /// </summary>
    public const int MaxPasswordLength = 256;

    public const int DefaultMinPasswordLength = 12;
    public const int DefaultMaxFailedLogins = 5;
    public const double DefaultLockoutMinutes = 15;
    public const int DefaultStampCacheSeconds = 30;
    public const int DefaultLoginRateLimitPerMinute = 10;
    public const double DefaultLoginRateLimitWindowSeconds = 60;

    /// <summary>
    /// The flat alias for <c>Auth:RegistrationEnabled</c>. Operators reach for this name, so it is
    /// accepted verbatim; the colon-keyed setting is canonical and wins when both are present.
    /// </summary>
    public const string RegistrationEnabledAlias = "REGISTRATIONS_ALLOWED";

    /// <summary>
    /// How long a lockout lasts, as the <see cref="TimeSpan"/> the endpoint actually computes with.
    /// <para>
    /// <b>Why <see cref="LockoutMinutes"/> binds as a fraction.</b> Two of the three counter rules
    /// (see <c>AuthEndpoints</c>' login doc) can only be proven by a test that lives through a whole
    /// lockout window and submits another attempt on the far side of it. Bound as an <c>int</c>, the
    /// smallest useful value would be a whole minute, and the suite would either wait that out or fake
    /// the expiry by writing <c>LockoutEndUtc</c> directly — which exercises the reset path without
    /// proving the window is honored, i.e. half the point. A fraction costs nothing in production, where
    /// nobody configures 0.05, and it keeps those tests honest.
    /// </para>
    /// </summary>
    public TimeSpan LockoutWindow => TimeSpan.FromMinutes(LockoutMinutes);

    /// <summary>
    /// Whether the lockout is armed at all. A configured <c>Auth:MaxFailedLogins</c> of <b>zero or
    /// less</b> means "no lockout" rather than "lock on the first failure" — the latter reading would
    /// turn a plausible typo in <c>.env</c> into an account nobody can sign in to, and
    /// <c>/auth/capabilities</c> reports the <c>0</c> honestly so a client can stop warning about a
    /// lockout that cannot happen.
    /// </summary>
    public bool LockoutEnabled => MaxFailedLogins > 0;

    /// <summary>
    /// <see cref="LockoutMinutes"/> as <c>/auth/capabilities</c> publishes it: whole minutes, rounded
    /// <b>up</b> so a sub-minute window never advertises as <c>0</c> ("try again in no time at all").
    /// </summary>
    public int ReportedLockoutMinutes => (int)Math.Ceiling(LockoutMinutes);

    /// <summary>
    /// The rate-limit window. <b>Separate from <see cref="LoginRateLimitPerMinute"/> for the tests'
    /// sake</b> — a suite that has to exhaust a limiter cannot afford to wait out a real minute before
    /// the next test's requests are let through. The operator-facing knob keeps its per-minute name and
    /// its per-minute meaning; only the suite ever moves the window.
    /// </summary>
    public TimeSpan LoginRateLimitWindow => TimeSpan.FromSeconds(LoginRateLimitWindowSeconds);

    public static AuthOptions Resolve(IConfiguration configuration) => new(
        RegistrationEnabled: Flag(configuration, "Auth:RegistrationEnabled", RegistrationEnabledAlias, @default: true),
        MinPasswordLength: Number(configuration, "Auth:MinPasswordLength", DefaultMinPasswordLength),
        MaxFailedLogins: Number(configuration, "Auth:MaxFailedLogins", DefaultMaxFailedLogins),
        LockoutMinutes: Fraction(configuration, "Auth:LockoutMinutes", DefaultLockoutMinutes),
        StampCacheSeconds: Number(configuration, "Auth:StampCacheSeconds", DefaultStampCacheSeconds),
        LoginRateLimitPerMinute: Number(configuration, "Auth:LoginRateLimitPerMinute", DefaultLoginRateLimitPerMinute),
        LoginRateLimitWindowSeconds: Fraction(
            configuration, "Auth:LoginRateLimitWindowSeconds", DefaultLoginRateLimitWindowSeconds));

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

    /// <summary>
    /// <c>InvariantCulture</c>, not the current culture's: the runtime image is Alpine and so runs
    /// invariant-globalization, where a comma-decimal <c>"0,05"</c> would not parse at all. Pinning it
    /// here means a value that works on a developer's machine works in the container.
    /// </summary>
    private static double Fraction(IConfiguration configuration, string key, double @default)
    {
        var value = Configured(configuration, key);
        return value is not null &&
               double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : @default;
    }

    private static string? Configured(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
