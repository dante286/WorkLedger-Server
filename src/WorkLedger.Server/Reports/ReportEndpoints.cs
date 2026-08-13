using System.Text;
using WorkLedger.Server.Auth;

namespace WorkLedger.Server.Reports;

/// <summary>
/// The three report endpoints. None is marked AllowAnonymous, so Program.cs's authorization fallback
/// policy protects them; the account they report on is the token's, never anything in the URL.
/// <para>
/// Route ids are <b>SyncIds</b> (the cross-device Guid), not the server's internal int Ids — the
/// <c>:guid</c> route constraint means a caller that sends an int gets a 404 rather than reaching a
/// handler that would have to guess which id it was looking at.
/// </para>
/// <para>
/// <c>from</c>/<c>to</c> are <c>yyyy-MM-dd</c> UTC dates and both ends are <b>inclusive</b>:
/// <c>from=2026-03-02&amp;to=2026-03-02</c> is that whole day. Either may be omitted; the default is
/// all-time (see ReportRange). Reports read; they never write, so every one of these is a plain GET.
/// </para>
/// </summary>
internal static class ReportEndpoints
{
    public static void MapReportEndpoints(this WebApplication app)
    {
        app.MapGet("/reports/weekly", async (
            string? weekOf,
            string? format,
            HttpRequest httpRequest,
            ReportService reports,
            ICurrentContext currentContext,
            CancellationToken cancellationToken) =>
        {
            if (!ReportFormats.TryResolve(format, httpRequest, out var resolvedFormat))
            {
                return UnknownFormat(format);
            }

            if (!ReportDates.TryParseDate(weekOf, out var parsedWeekOf))
            {
                return InvalidDate("weekOf", weekOf);
            }

            var report = await reports.WeeklyAsync(currentContext.AccountId, parsedWeekOf, cancellationToken);

            return resolvedFormat == ReportFormat.Csv
                ? Csv(ReportCsv.Weekly(report.Grid), $"workledger-weekly-{report.Dto.WeekStart:yyyy-MM-dd}.csv")
                : Results.Ok(report.Dto);
        });

        app.MapGet("/reports/projects/{projectSyncId:guid}", async (
            Guid projectSyncId,
            string? from,
            string? to,
            string? format,
            HttpRequest httpRequest,
            ReportService reports,
            ICurrentContext currentContext,
            CancellationToken cancellationToken) =>
        {
            if (!ReportFormats.TryResolve(format, httpRequest, out var resolvedFormat))
            {
                return UnknownFormat(format);
            }

            if (!TryParseRange(from, to, out var range, out var error))
            {
                return error;
            }

            var report = await reports.ProjectAsync(currentContext.AccountId, projectSyncId, range, cancellationToken);
            if (report is null)
            {
                return Results.NotFound(new { error = $"No project with SyncId '{projectSyncId}'." });
            }

            return resolvedFormat == ReportFormat.Csv
                ? Csv(ReportCsv.Project(report), $"workledger-project-{projectSyncId}.csv")
                : Results.Ok(report);
        });

        app.MapGet("/reports/tasks/{taskSyncId:guid}", async (
            Guid taskSyncId,
            string? from,
            string? to,
            string? format,
            HttpRequest httpRequest,
            ReportService reports,
            ICurrentContext currentContext,
            CancellationToken cancellationToken) =>
        {
            if (!ReportFormats.TryResolve(format, httpRequest, out var resolvedFormat))
            {
                return UnknownFormat(format);
            }

            if (!TryParseRange(from, to, out var range, out var error))
            {
                return error;
            }

            var report = await reports.TaskAsync(currentContext.AccountId, taskSyncId, range, cancellationToken);
            if (report is null)
            {
                return Results.NotFound(new { error = $"No task with SyncId '{taskSyncId}'." });
            }

            return resolvedFormat == ReportFormat.Csv
                ? Csv(ReportCsv.Task(report), $"workledger-task-{taskSyncId}.csv")
                : Results.Ok(report);
        });
    }

    private static bool TryParseRange(string? from, string? to, out ReportRange range, out IResult error)
    {
        range = ReportRange.Of(null, null);

        if (!ReportDates.TryParseDate(from, out var parsedFrom))
        {
            error = InvalidDate("from", from);
            return false;
        }

        if (!ReportDates.TryParseDate(to, out var parsedTo))
        {
            error = InvalidDate("to", to);
            return false;
        }

        if (parsedFrom is not null && parsedTo is not null && parsedFrom > parsedTo)
        {
            // Silently returning an empty report would look identical to "you logged no time then",
            // which is the wrong thing to tell someone who typed the dates backwards.
            error = Results.BadRequest(new { error = $"'from' ({from}) is after 'to' ({to})." });
            return false;
        }

        range = ReportRange.Of(parsedFrom, parsedTo);
        error = Results.Empty;
        return true;
    }

    /// <summary>
    /// Results.File (rather than Results.Text) so the response carries a
    /// <c>Content-Disposition: attachment</c> with a filename — S4 asks for a download, and a browser
    /// hitting the URL should save the file rather than render it as a wall of text.
    /// </summary>
    private static IResult Csv(string csv, string fileName) =>
        Results.File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", fileName);

    private static IResult InvalidDate(string parameter, string? raw) =>
        Results.BadRequest(new
        {
            error = $"'{parameter}' must be a yyyy-MM-dd date no later than {ReportDates.Latest:yyyy-MM-dd}: '{raw}'.",
        });

    private static IResult UnknownFormat(string? format) =>
        Results.BadRequest(new { error = $"Unknown 'format' '{format}'. Supported: json (default), csv." });
}
