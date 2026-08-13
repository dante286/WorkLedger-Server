using Microsoft.Net.Http.Headers;

namespace WorkLedger.Server.Reports;

internal enum ReportFormat
{
    Json,
    Csv,
}

/// <summary>
/// Which representation the caller asked for. Two ways in, because the two callers are different:
/// <c>?format=csv</c> is what a person pastes into a browser or a shell to get a file, and
/// <c>Accept: text/csv</c> is what a program sends. The query parameter wins when both are present —
/// it is the explicit one, typed for this request, whereas Accept is often a client-wide default.
/// <para>
/// JSON is the default, and an unrecognized <c>format</c> is a 400 rather than a silent fallback to it:
/// a typo like <c>format=CSVs</c> should not hand back JSON that a caller's CSV parser then reports as
/// a corrupt file.
/// </para>
/// </summary>
internal static class ReportFormats
{
    public static bool TryResolve(string? format, HttpRequest request, out ReportFormat resolved)
    {
        if (!string.IsNullOrWhiteSpace(format))
        {
            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                resolved = ReportFormat.Csv;
                return true;
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                resolved = ReportFormat.Json;
                return true;
            }

            resolved = ReportFormat.Json;
            return false;
        }

        resolved = PrefersCsv(request) ? ReportFormat.Csv : ReportFormat.Json;
        return true;
    }

    /// <summary>
    /// Only an explicit <c>text/csv</c> counts. A wildcard <c>Accept: */*</c> — which is what curl and
    /// most HTTP clients send by default — means "anything", and answering that with a file download
    /// would surprise every caller who never asked for one.
    /// </summary>
    private static bool PrefersCsv(HttpRequest request) =>
        MediaTypeHeaderValue.TryParseList(request.Headers.Accept, out var mediaTypes)
        && mediaTypes.Any(mediaType => mediaType.MediaType.Equals("text/csv", StringComparison.OrdinalIgnoreCase));
}
