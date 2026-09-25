using System.Globalization;
using Microsoft.AspNetCore.Html;

namespace WorkLedger.Server.Web;

/// <summary>
/// Formatting for portal pages.
/// <para>
/// <b>Timestamps render as UTC, and the browser localizes them.</b> The runtime image runs
/// invariant-globalization (see <c>CLAUDE.md</c>), so the server cannot format a date in the reader's
/// locale or time zone — and has no business guessing either. It writes ISO-8601 UTC into a
/// <c>&lt;time datetime&gt;</c> element and <c>wwwroot/portal/assets/portal.js</c> rewrites the text in
/// the reader's own terms. Without JavaScript the page still shows the correct time, in UTC.
/// </para>
/// </summary>
public static class PortalFormat
{
    public static IHtmlContent Time(DateTime value)
    {
        // timestamptz columns come back as Utc; anything Unspecified is treated as UTC, never as local.
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        var machine = utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var human = utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        // Both strings are built from fixed formats over a DateTime, so there is nothing here to encode.
        return new HtmlString($"<time datetime=\"{machine}\">{human}</time>");
    }
}
