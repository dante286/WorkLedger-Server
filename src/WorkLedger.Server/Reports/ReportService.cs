using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Reports;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Reports;

/// <summary>
/// The reporting rules, in one place. All three endpoints share them; the WHERE clause in
/// <see cref="ReportQuery"/> is their SQL form.
///
/// <para><b>1. Account scope.</b> AccountId comes from the caller's token. Nothing in a route or query
/// string can widen it.</para>
///
/// <para><b>2. Tombstones are excluded — the whole chain, not just the entry.</b> A time entry counts
/// only if it, its task, and that task's project are all alive. Time logged against a task the user
/// deleted is not time they want back in their weekly total, and a per-project report that included it
/// could not name the project it belonged to anyway.</para>
///
/// <para><b>3. An entry with an unresolvable parent does not count.</b> The joins are inner joins on
/// SyncId, so an entry whose task (or whose task's project) has not arrived on this server yet
/// contributes nothing. This is transient — push applies parents before children, and the missing rows
/// land on a later sync — and the alternative, an "unattributed" bucket, would mean the account total
/// stopped being the sum of the per-project totals. Consistency between the views is worth more than
/// surfacing a few seconds that are about to be attributed properly.</para>
///
/// <para><b>4. Running entries are excluded, and counted.</b> The client writes DurationSeconds only
/// when a timer stops (TimerService) — a running entry has a null EndTime <i>and</i> a null duration, so
/// it has no fixed length to sum. Counting "now − start" would make the same report return a different
/// number every second and disagree with the client's own rollup, which filters on
/// <c>EndTime != null</c> too. Every DTO therefore carries a RunningEntries count so a caller can say
/// "plus one timer still going" rather than silently under-reporting.</para>
///
/// <para><b>5. Duration is the stored DurationSeconds, falling back to EndTime − StartTime.</b> The
/// stored column is what the client sums, so the server agrees with the client by using the same
/// number; the fallback catches a finished row that somehow lacks one rather than counting it as
/// zero.</para>
///
/// <para><b>6. An entry lands wholly in the UTC day it started.</b> Bucketing by StartTime matches the
/// client (its weekly rollup filters <c>entry.StartTime &gt;= weekStart</c>), and it means an entry
/// spanning midnight is not split across two days — splitting would be more accurate but would disagree
/// with the client for no benefit anyone asked for. Days and weeks are UTC; the caller localizes.</para>
/// </summary>
/// <summary>
/// The weekly report plus the (day × project × task) grid it was folded from. The DTO's three
/// breakdowns cannot be re-crossed back into that grid — a per-project total and a per-day total do not
/// say which project the Tuesday hours belonged to — and the CSV export is exactly that grid, so it is
/// carried alongside rather than re-queried.
/// </summary>
internal sealed record WeeklyReport(WeeklyReportDto Dto, IReadOnlyList<ReportBucket> Grid);

internal sealed class ReportService(WorkLedgerDbContext db)
{
    private readonly ReportQuery _query = new(db);

    /// <summary>
    /// The ISO week (Monday–Sunday, UTC) containing <paramref name="weekOf"/>, or the current week when
    /// it is null.
    /// </summary>
    public async Task<WeeklyReport> WeeklyAsync(int accountId, DateOnly? weekOf, CancellationToken cancellationToken)
    {
        var weekStart = ReportDates.StartOfIsoWeek(weekOf ?? DateOnly.FromDateTime(DateTime.UtcNow));
        var weekEnd = weekStart.AddDays(6);

        var filter = new ReportFilter(accountId, ReportRange.Bounded(weekStart, weekEnd));
        var buckets = await _query.BucketsAsync(filter, cancellationToken);
        var running = await _query.RunningEntryCountAsync(filter, cancellationToken);

        // Zero-filled across the fixed seven-day frame so a caller can chart the week directly.
        var byDay = buckets.GroupBy(b => b.Day).ToDictionary(g => g.Key, g => g.Sum(b => b.Seconds));
        var days = Enumerable.Range(0, 7)
            .Select(offset => weekStart.AddDays(offset))
            .Select(date => new DailyTotalDto(date, byDay.GetValueOrDefault(date)))
            .ToList();

        var dto = new WeeklyReportDto(
            weekStart,
            weekEnd,
            buckets.Sum(b => b.Seconds),
            ProjectTotals(buckets),
            TaskTotals(buckets),
            days,
            running);

        return new WeeklyReport(dto, buckets);
    }

    /// <summary>
    /// One project, with a per-task breakdown. Returns null when the project does not exist for this
    /// account or is tombstoned — the caller turns that into a 404, which is also the answer a device
    /// asking about another account's project gets.
    /// </summary>
    public async Task<ProjectReportDto?> ProjectAsync(
        int accountId, Guid projectSyncId, ReportRange range, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.AccountId == accountId && p.SyncId == projectSyncId && !p.IsDeleted, cancellationToken);

        if (project is null)
        {
            return null;
        }

        var filter = new ReportFilter(accountId, range, ProjectSyncId: projectSyncId);
        var buckets = await _query.BucketsAsync(filter, cancellationToken);
        var running = await _query.RunningEntryCountAsync(filter, cancellationToken);

        return new ProjectReportDto(
            projectSyncId,
            project.Name,
            range.From,
            range.To,
            buckets.Sum(b => b.Seconds),
            TaskTotals(buckets),
            running);
    }

    /// <summary>One task, with a daily breakdown. Null means 404, as above.</summary>
    public async Task<TaskReportDto?> TaskAsync(
        int accountId, Guid taskSyncId, ReportRange range, CancellationToken cancellationToken)
    {
        // Joined to the project the same way the aggregation does — an alive task under a tombstoned
        // project reports nothing, so answering 200 with a total of zero would be a lie about why.
        var task = await db.TaskItems
            .AsNoTracking()
            .Where(t => t.AccountId == accountId && t.SyncId == taskSyncId && !t.IsDeleted)
            .Join(
                db.Projects.AsNoTracking().Where(p => p.AccountId == accountId && !p.IsDeleted),
                t => t.ProjectSyncId,
                p => p.SyncId,
                (t, p) => new { t.Name, ProjectSyncId = p.SyncId, ProjectName = p.Name })
            .SingleOrDefaultAsync(cancellationToken);

        if (task is null)
        {
            return null;
        }

        var filter = new ReportFilter(accountId, range, TaskSyncId: taskSyncId);
        var buckets = await _query.BucketsAsync(filter, cancellationToken);
        var running = await _query.RunningEntryCountAsync(filter, cancellationToken);

        return new TaskReportDto(
            taskSyncId,
            task.Name,
            task.ProjectSyncId,
            task.ProjectName,
            range.From,
            range.To,
            buckets.Sum(b => b.Seconds),
            buckets.GroupBy(b => b.Day)
                .Select(g => new DailyTotalDto(g.Key, g.Sum(b => b.Seconds)))
                .OrderBy(d => d.Date)
                .ToList(),
            running);
    }

    private static List<ProjectTotalDto> ProjectTotals(IReadOnlyList<ReportBucket> buckets) => buckets
        .GroupBy(b => (b.ProjectSyncId, b.ProjectName))
        .Select(g => new ProjectTotalDto(g.Key.ProjectSyncId, g.Key.ProjectName, g.Sum(b => b.Seconds)))
        .OrderBy(p => p.ProjectName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static List<TaskTotalDto> TaskTotals(IReadOnlyList<ReportBucket> buckets) => buckets
        .GroupBy(b => (b.TaskSyncId, b.TaskName, b.ProjectSyncId, b.ProjectName))
        .Select(g => new TaskTotalDto(
            g.Key.TaskSyncId, g.Key.TaskName, g.Key.ProjectSyncId, g.Key.ProjectName, g.Sum(b => b.Seconds)))
        .OrderBy(t => t.ProjectName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(t => t.TaskName, StringComparer.OrdinalIgnoreCase)
        .ToList();
}
