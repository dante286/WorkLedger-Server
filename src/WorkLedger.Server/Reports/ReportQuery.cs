using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Reports;

/// <summary>One (UTC day × task) bucket — the finest grain any of the three reports needs.</summary>
internal sealed record ReportBucket(
    DateOnly Day,
    Guid ProjectSyncId,
    string? ProjectName,
    Guid TaskSyncId,
    string? TaskName,
    long Seconds);

/// <summary>
/// The one aggregation every report is folded from, plus the running-entry count that goes with it.
///
/// <para><b>Why raw SQL.</b> Two things here are awkward or unsafe to express through EF's LINQ
/// translation. First, the day bucket: the timestamp columns are <c>timestamptz</c>, and the obvious
/// <c>StartTime.Date</c> / <c>.Year|.Month|.Day</c> translations either fail or truncate through the
/// <i>session's</i> TimeZone, which would make a report's day boundaries depend on the server's
/// configuration. <c>AT TIME ZONE 'UTC'</c> here is explicit and cannot drift. Second, the two joins are
/// on (UserId, SyncId) pairs with no navigation properties to ride on — the S1 model links parents by
/// SyncId, deliberately, and declares no EF relationships between Project/TaskItem/TimeEntry. S4 blesses
/// raw SQL for exactly this.</para>
///
/// <para><b>Why one query and not four.</b> The weekly report needs a user total, per-project,
/// per-task and per-day figures. Grouping once at (day, task) grain and folding the result in memory
/// keeps all four views arithmetically consistent by construction, and the fold is over a result set
/// bounded by days × tasks — the <c>TimeEntries</c> table itself is summed in Postgres and never
/// materialized, which is the footgun S4 calls out.</para>
///
/// <para><b>What counts.</b> See <see cref="ReportService"/> — the WHERE clause below is that policy.</para>
/// </summary>
internal sealed class ReportQuery(WorkLedgerDbContext db)
{
    private const string From = """
        FROM "TimeEntries" te
        JOIN "TaskItems" t ON t."UserId" = te."UserId"
                          AND t."SyncId" = te."TaskSyncId"
                          AND t."IsDeleted" = FALSE
        JOIN "Projects" p ON p."UserId" = t."UserId"
                         AND p."SyncId" = t."ProjectSyncId"
                         AND p."IsDeleted" = FALSE
        WHERE te."UserId" = @userId
          AND te."IsDeleted" = FALSE
          AND te."StartTime" >= @fromUtc
          AND te."StartTime" < @toUtcExclusive
        """;

    public async Task<IReadOnlyList<ReportBucket>> BucketsAsync(
        ReportFilter filter, CancellationToken cancellationToken)
    {
        // COALESCE, not a plain subtraction: the client persists DurationSeconds when it stops a timer
        // (TimerService) and when an entry is edited by hand, so that column is the authoritative
        // duration and is what the client's own rollup sums. End - Start is the fallback for a row that
        // somehow has an EndTime but no stored duration, so such an entry contributes its real time
        // instead of silently counting as zero.
        var sql = $"""
            SELECT (te."StartTime" AT TIME ZONE 'UTC')::date AS "Day",
                   p."SyncId" AS "ProjectSyncId",
                   p."Name" AS "ProjectName",
                   t."SyncId" AS "TaskSyncId",
                   t."Name" AS "TaskName",
                   SUM(COALESCE(te."DurationSeconds",
                                EXTRACT(EPOCH FROM (te."EndTime" - te."StartTime"))::bigint))::bigint AS "Seconds"
            {From}
              AND te."EndTime" IS NOT NULL
            {ScopePredicates(filter)}
            GROUP BY 1, 2, 3, 4, 5
            ORDER BY 1, 3, 5
            """;

        var buckets = new List<ReportBucket>();

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(sql, filter);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                buckets.Add(new ReportBucket(
                    reader.GetFieldValue<DateOnly>(0),
                    reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5)));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return buckets;
    }

    /// <summary>
    /// Entries still running (no EndTime) inside the window. They are excluded from every total — see
    /// <see cref="ReportService"/> — but reporting nothing about them would leave a user staring at a
    /// number that is quietly short by however long their current timer has been going.
    /// </summary>
    public async Task<int> RunningEntryCountAsync(ReportFilter filter, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT COUNT(*)::int
            {From}
              AND te."EndTime" IS NULL
            {ScopePredicates(filter)}
            """;

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(sql, filter);
            return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Appended rather than written as <c>(@p IS NULL OR ...)</c>: an untyped NULL parameter gives
    /// Postgres nothing to infer a type from, and leaving the clause out entirely also spares the
    /// planner a predicate it would have to prove is a no-op.
    /// </summary>
    private static string ScopePredicates(ReportFilter filter)
    {
        var predicates = string.Empty;

        if (filter.ProjectSyncId is not null)
        {
            predicates += "\n  AND p.\"SyncId\" = @projectSyncId";
        }

        if (filter.TaskSyncId is not null)
        {
            predicates += "\n  AND t.\"SyncId\" = @taskSyncId";
        }

        return predicates;
    }

    private DbCommand CreateCommand(string sql, ReportFilter filter)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;

        Add(command, "userId", filter.UserId);
        Add(command, "fromUtc", filter.Range.FromUtc);
        Add(command, "toUtcExclusive", filter.Range.ToUtcExclusive);

        if (filter.ProjectSyncId is not null)
        {
            Add(command, "projectSyncId", filter.ProjectSyncId.Value);
        }

        if (filter.TaskSyncId is not null)
        {
            Add(command, "taskSyncId", filter.TaskSyncId.Value);
        }

        return command;
    }

    /// <summary>
    /// DbType is deliberately left unset. Npgsql infers the Postgres type from the CLR value, and for
    /// DateTime that inference keys off <see cref="DateTimeKind"/>: a Utc-kind value becomes
    /// <c>timestamptz</c>, which is what the columns are. Forcing <see cref="DbType.DateTime"/> would
    /// instead send <c>timestamp without time zone</c>, and comparing that against a <c>timestamptz</c>
    /// column makes Postgres interpret it through the <i>session's</i> TimeZone — the exact
    /// server-configuration dependency the UTC bucketing above exists to avoid. ReportRange guarantees
    /// the Kind.
    /// </summary>
    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>
/// UserId always comes from the caller's token, never from the request — the same rule the sync
/// endpoints hold to, and the reason a device cannot address another user's rows.
/// </summary>
internal sealed record ReportFilter(int UserId, ReportRange Range, Guid? ProjectSyncId = null, Guid? TaskSyncId = null);
