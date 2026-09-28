using System.Globalization;
using System.Net;

namespace WorkLedger.Server.Web;

/// <summary>
/// The web portal's settings (S11), resolved once and eagerly at startup like every other piece of
/// configuration here, so a misconfigured portal exits the container naming the key.
/// <para>
/// <b>Off by default, and off means absent.</b> With <see cref="Enabled"/> false no page, static asset,
/// header middleware or persisted key ring is wired up, and every existing request behaves exactly as it
/// did before S11. Publishing a new sign-in form on an internet-facing box is the operator's decision,
/// in the same way S10 left the signup secret on until they turned it off.
/// </para>
/// <para>
/// A <b>blank</b> value counts as unset throughout, for the reason <c>PostgresConnection</c> and
/// <c>AuthOptions</c> give: compose renders an unset optional override as <c>""</c>.
/// </para>
/// </summary>
/// <param name="Enabled"><c>Web:Enabled</c>. Default false.</param>
/// <param name="PublicBaseUrl">
/// <c>Web:PublicBaseUrl</c>, e.g. <c>https://ledger.example.com</c>, without a trailing slash; null only
/// while the portal is off. <b>The only source for an absolute link</b> — an invite or reset link, or the
/// portal address a client is told about. <b>Never build one from the request's <c>Host</c> header</b>:
/// behind a proxy it is whatever the caller sent, and a link minted from it is a link an attacker chose.
/// Links inside the portal are relative and need neither.
/// </param>
/// <param name="SessionHours">
/// <c>Web:SessionHours</c>: the portal session's <b>sliding</b> lifetime. Default 8. There is deliberately
/// no "remember me" — a portal session is for a task (issue an invite, remove a device), not for staying
/// signed in, and the long-lived credential in this system is the desktop client's token.
/// </param>
public sealed record WebOptions(bool Enabled, string? PublicBaseUrl, int SessionHours)
{
    public const int DefaultSessionHours = 8;

    /// <summary>Where the portal lives. <c>/admin/*</c> is the JSON API's and does not move.</summary>
    public const string PortalPath = "/portal";

    public TimeSpan SessionLifetime => TimeSpan.FromHours(SessionHours);

    /// <summary>
    /// The portal's absolute address, as <c>GET /auth/capabilities</c> advertises it to the desktop
    /// client, or null while the portal is off. Built from <see cref="PublicBaseUrl"/> for the reason
    /// every absolute link is.
    /// </summary>
    public string? PortalUrl => Enabled ? $"{PublicBaseUrl}{PortalPath}" : null;

    public static WebOptions Resolve(IConfiguration configuration)
    {
        var enabledValue = Configured(configuration, "Web:Enabled");
        var enabled = enabledValue is not null && bool.TryParse(enabledValue, out var parsed) && parsed;

        var sessionValue = Configured(configuration, "Web:SessionHours");
        var sessionHours = sessionValue is not null &&
            int.TryParse(sessionValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
                ? hours
                : DefaultSessionHours;

        if (!enabled)
        {
            // Nothing else is read or validated while the portal is off: an operator who has not turned
            // it on must not be refused startup over a key they have never heard of.
            return new WebOptions(false, null, sessionHours);
        }

        if (sessionHours <= 0)
        {
            throw new InvalidOperationException(
                "Web:SessionHours must be a whole number of hours greater than zero (or unset, for the " +
                $"default of {DefaultSessionHours}).");
        }

        return new WebOptions(true, ResolvePublicBaseUrl(Configured(configuration, "Web:PublicBaseUrl")), sessionHours);
    }

    /// <summary>
    /// Absolute, <c>https://</c> — or <c>http://</c> for a loopback host, which is development and which
    /// browsers treat as a secure context — with no query, fragment or credentials. Refused rather than
    /// repaired, because every link the portal hands out is built from it and a guessed-at correction
    /// would be wrong in a way nobody notices until a link fails.
    /// </summary>
    private static string ResolvePublicBaseUrl(string? value)
    {
        const string Key = "Web:PublicBaseUrl (or Web__PublicBaseUrl env var)";

        if (value is null)
        {
            throw new InvalidOperationException(
                $"{Key} is required when Web:Enabled is true. Set it to the address browsers reach this " +
                "server at, e.g. https://ledger.example.com — it is the only source for the links the " +
                "portal hands out, which are never built from the request's Host header.");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                $"{Key} must be an absolute https:// URL with no query or fragment, e.g. " +
                $"https://ledger.example.com. Got '{value}'.");
        }

        if (uri.Scheme == "http" && !IsLoopback(uri))
        {
            throw new InvalidOperationException(
                $"{Key} must use https:// — the portal's session cookie is Secure-only, so a browser " +
                $"would never send it back over plain http. Only a localhost address may use http://. Got '{value}'.");
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback ||
        (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private static string? Configured(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
