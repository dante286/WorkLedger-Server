using System.Globalization;
using System.Text;
using WorkLedger.Contracts.Reports;

namespace WorkLedger.Server.Reports;

/// <summary>
/// RFC 4180 CSV for the three reports. Each file is that report's own breakdown flattened one row per
/// bucket, plus the names needed to identify the row once the file is detached from the request that
/// produced it:
/// <list type="bullet">
/// <item><c>/reports/weekly</c> → <c>date,project,task,seconds</c></item>
/// <item><c>/reports/projects/{id}</c> → <c>project,task,seconds</c></item>
/// <item><c>/reports/tasks/{id}</c> → <c>date,project,task,seconds</c></item>
/// </list>
/// <para>
/// The weekly file is the (day × project × task) grid the JSON's three breakdowns are each folded from,
/// so every one of them is a single pivot away in a spreadsheet — which is why this is one file and not
/// three. Days with no time have no row: the JSON zero-fills its seven-day frame so a caller can chart
/// it without filling gaps, but a CSV row saying "nothing happened" is noise to a reader summing a
/// column.
/// </para>
/// <para>
/// Nothing here writes the RunningEntries count. CSV is a table of recorded time; a header-level scalar
/// has no honest column to live in, and a caller who needs it reads the JSON.
/// </para>
/// </summary>
internal static class ReportCsv
{
    public static string Weekly(IReadOnlyList<ReportBucket> grid)
    {
        var builder = NewBuilder("date", "project", "task", "seconds");

        foreach (var bucket in grid)
        {
            Row(builder, Field(bucket.Day), Field(bucket.ProjectName), Field(bucket.TaskName), bucket.Seconds);
        }

        return builder.ToString();
    }

    public static string Project(ProjectReportDto report)
    {
        var builder = NewBuilder("project", "task", "seconds");

        foreach (var task in report.Tasks)
        {
            Row(builder, Field(task.ProjectName), Field(task.TaskName), task.Seconds);
        }

        return builder.ToString();
    }

    public static string Task(TaskReportDto report)
    {
        var builder = NewBuilder("date", "project", "task", "seconds");

        foreach (var day in report.Days)
        {
            Row(builder, Field(day.Date), Field(report.ProjectName), Field(report.TaskName), day.Seconds);
        }

        return builder.ToString();
    }

    private static StringBuilder NewBuilder(params string[] headers)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(',', headers)).Append("\r\n");
        return builder;
    }

    private static void Row(StringBuilder builder, params object[] fields) =>
        builder.Append(string.Join(',', fields)).Append("\r\n");

    private static string Field(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Project and task names are free text a user typed. A name containing a comma, a quote or a
    /// newline would otherwise shift every following column on that row — silently, because the file
    /// still parses.
    /// </summary>
    private static string Field(string? value)
    {
        var text = value ?? string.Empty;

        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }
}
