namespace WorkLedger.Contracts.Reports;

// The authoritative, cross-device rollup (plans/S4-reports.md). These live in Contracts so the client
// can consume the server's numbers directly if its local Reports tab ever defers to them — today it
// does not (client Plan 7 keeps its own local rollup), so nothing on the client references these yet.
//
// Unlike SyncChangeDto.PayloadJson, which is PascalCase by hand-rolled policy (see SyncJson), these are
// serialized by ASP.NET Core's web defaults like every other envelope on this server — camelCase out,
// case-insensitive in. There is no case-sensitive reader on the other end to break.
//
// Every duration is whole seconds and every timestamp/date is UTC; the caller localizes. Definitions
// that govern all three reports — which entries count, and which day an entry lands in — are documented
// on ReportService in the server project, and restated in the README.

/// <summary>One UTC day's total. <paramref name="Date"/> is the day the entry <i>started</i>.</summary>
public sealed record DailyTotalDto(DateOnly Date, long Seconds);

public sealed record ProjectTotalDto(Guid ProjectSyncId, string? ProjectName, long Seconds);

/// <summary>
/// Carries its parent project as well as the task: a task name alone ("Review") is not identifying,
/// and the CSV rows are flat.
/// </summary>
public sealed record TaskTotalDto(
    Guid TaskSyncId,
    string? TaskName,
    Guid ProjectSyncId,
    string? ProjectName,
    long Seconds);

/// <summary>
/// One ISO week (Monday–Sunday, UTC). <see cref="Days"/> always has exactly seven entries, including
/// days with zero seconds, so a caller can chart the week without filling gaps itself.
/// <para>
/// <see cref="TotalSeconds"/> is the sum of <see cref="Projects"/>, which is the sum of
/// <see cref="Tasks"/>, which is the sum of <see cref="Days"/> — all four views are folded from one
/// aggregation, so they cannot disagree.
/// </para>
/// </summary>
public sealed record WeeklyReportDto(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    long TotalSeconds,
    IReadOnlyList<ProjectTotalDto> Projects,
    IReadOnlyList<TaskTotalDto> Tasks,
    IReadOnlyList<DailyTotalDto> Days,
    int RunningEntries);

/// <summary>
/// One project over an optional date range. <see cref="From"/>/<see cref="To"/> are null when that end
/// of the range was left open — the default is all-time.
/// </summary>
public sealed record ProjectReportDto(
    Guid ProjectSyncId,
    string? ProjectName,
    DateOnly? From,
    DateOnly? To,
    long TotalSeconds,
    IReadOnlyList<TaskTotalDto> Tasks,
    int RunningEntries);

/// <summary>
/// One task over an optional date range. Unlike <see cref="WeeklyReportDto.Days"/>, this daily
/// breakdown lists only days that actually have time: the range here can be open-ended, so there is no
/// bounded frame to zero-fill.
/// </summary>
public sealed record TaskReportDto(
    Guid TaskSyncId,
    string? TaskName,
    Guid ProjectSyncId,
    string? ProjectName,
    DateOnly? From,
    DateOnly? To,
    long TotalSeconds,
    IReadOnlyList<DailyTotalDto> Days,
    int RunningEntries);
