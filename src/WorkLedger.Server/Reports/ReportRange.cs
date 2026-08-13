using System.Globalization;

namespace WorkLedger.Server.Reports;

/// <summary>
/// The half-open UTC instant window a report aggregates over, plus the calendar dates it was asked for.
/// <para>
/// Query parameters are calendar <i>dates</i> (<c>yyyy-MM-dd</c>) but the column being filtered is a
/// <c>timestamptz</c>, so every range is widened here exactly once: <c>from</c> starts at that day's
/// 00:00:00Z and <c>to</c> ends at the <i>end</i> of that day, i.e. the exclusive upper bound is the
/// following midnight. A caller asking <c>from=2026-03-02&amp;to=2026-03-02</c> means "that whole day",
/// not "the single instant of midnight".
/// </para>
/// <para>
/// An open end is represented as <see cref="DateTime.MinValue"/>/<see cref="DateTime.MaxValue"/> rather
/// than null so the SQL never needs a nullable parameter — a NULL parameter in a
/// <c>(@p IS NULL OR col &gt;= @p)</c> predicate has no inferable type on the Postgres side, and both
/// sentinels are comfortably inside <c>timestamptz</c>'s range.
/// </para>
/// </summary>
internal sealed record ReportRange(DateOnly? From, DateOnly? To, DateTime FromUtc, DateTime ToUtcExclusive)
{
    private static readonly DateTime Beginning = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
    private static readonly DateTime End = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

    public static ReportRange Bounded(DateOnly from, DateOnly to) => new(
        from,
        to,
        StartOfDayUtc(from),
        StartOfDayUtc(to.AddDays(1)));

    public static ReportRange Of(DateOnly? from, DateOnly? to) => new(
        from,
        to,
        from is null ? Beginning : StartOfDayUtc(from.Value),
        to is null ? End : StartOfDayUtc(to.Value.AddDays(1)));

    private static DateTime StartOfDayUtc(DateOnly date) =>
        DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
}

internal static class ReportDates
{
    /// <summary>
    /// The Monday on or before <paramref name="date"/>. ISO-8601 weeks run Monday–Sunday, and
    /// <see cref="DayOfWeek"/> numbers them from Sunday, hence the shift.
    /// </summary>
    public static DateOnly StartOfIsoWeek(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>
    /// The last date this API accepts. Both the ISO-week expansion (Monday + 6 days) and the inclusive
    /// range end (the day + 1) do date arithmetic <i>past</i> whatever the caller sent, and running that
    /// off the end of <see cref="DateOnly"/> throws — which would turn a parseable, if absurd, query
    /// string into a 500. Rejecting the final year up front makes it a 400 with a reason instead.
    /// </summary>
    public static readonly DateOnly Latest = new(9998, 12, 31);

    /// <summary>
    /// Parses a <c>yyyy-MM-dd</c> query parameter. A missing or empty value is not an error — it means
    /// "unbounded" for a range end, or "this week" for <c>weekOf</c>; the caller decides which.
    /// </summary>
    public static bool TryParseDate(string? raw, out DateOnly? date)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            date = null;
            return true;
        }

        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && parsed <= Latest)
        {
            date = parsed;
            return true;
        }

        date = null;
        return false;
    }
}
