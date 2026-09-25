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
/// </summary>
public sealed class PortalLinks(WebOptions web)
{
    public string Invite(string token) => $"{BaseUrl}{WebOptions.PortalPath}/register#invite={token}";

    public string PasswordReset(string token) => $"{BaseUrl}{WebOptions.PortalPath}/reset#token={token}";

    private string BaseUrl =>
        web.PublicBaseUrl
        ?? throw new InvalidOperationException("Portal links need Web:PublicBaseUrl, which is only set while Web:Enabled is on.");
}
