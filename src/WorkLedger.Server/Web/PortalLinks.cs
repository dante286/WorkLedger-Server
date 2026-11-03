namespace WorkLedger.Server.Web;

/// <summary>
/// The absolute links the portal hands out for someone else to open — invites and password resets.
/// <para>
/// <b>Built from <c>Web:PublicBaseUrl</c> and nothing else.</b> Never from the request's <c>Host</c>
/// header, which behind a proxy is whatever the caller sent: an admin tricked into issuing an invite from
/// a request with a forged <c>Host</c> would otherwise hand a colleague a link to an attacker's lookalike.
/// </para>
/// <para>
/// <b>The token rides in the fragment</b> (<c>#invite=…</c>, <c>#token=…</c>). A browser never sends the
/// fragment to the server, so the token cannot reach the request log, a proxy's access log or a
/// <c>Referer</c> header; the page it opens copies it into a form field with a static script, and it is
/// spent only when that form is posted. A token must never be a path segment — the request log records
/// paths. Tokens are base64url (see <c>SingleUseToken</c>), so they need no escaping here.
/// </para>
/// <para>
/// From S12 these links also leave the server in email (<c>EmailMessages</c>), which is safe for the same
/// two reasons: the token is in the fragment, and no GET spends one, so a mail scanner that opens every
/// link before the recipient does burns nothing.
/// </para>
/// </summary>
public sealed class PortalLinks(WebOptions web)
{
    public string Invite(string token) => $"{RegisterPage}#invite={token}";

    public string PasswordReset(string token) => $"{ResetPage}#token={token}";

    /// <summary>
    /// The registration page on its own, for an emailed invite's "paste this code" fallback — a mail
    /// gateway that rewrites links may drop the fragment (see <c>EmailMessages</c>).
    /// </summary>
    public string RegisterPage => $"{BaseUrl}{WebOptions.PortalPath}/register";

    /// <summary>The reset page on its own, for the same fallback as <see cref="RegisterPage"/>.</summary>
    public string ResetPage => $"{BaseUrl}{WebOptions.PortalPath}/reset";

    private string BaseUrl =>
        web.PublicBaseUrl
        ?? throw new InvalidOperationException("Portal links need Web:PublicBaseUrl, which is only set while Web:Enabled is on.");
}
