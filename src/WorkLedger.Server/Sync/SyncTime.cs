using System.Globalization;

namespace WorkLedger.Server.Sync;

/// <summary>
/// Every timestamp crossing the sync boundary passes through here before it reaches EF.
/// <para>
/// The synced columns are <c>timestamptz</c>, and Npgsql throws on a DateTime whose Kind is not Utc.
/// The client stores its timestamps through SQLite, which does not round-trip DateTimeKind, so it
/// serializes values with no offset and they arrive as <see cref="DateTimeKind.Unspecified"/>. That
/// is the normal case here, not an edge case — an unspecified value is treated as already-UTC
/// (which it is; the client writes UtcNow) rather than reinterpreted through the server's local
/// zone, which would shift it by the server's offset.
/// </para>
/// </summary>
internal static class SyncTime
{
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static DateTime? ToUtc(DateTime? value) => value.HasValue ? ToUtc(value.Value) : null;

    /// <summary>
    /// Parses the pull cursor. A missing or empty value means "everything" — that is a legitimate
    /// first request from a client that has never synced, not an error. (The client persists the
    /// <c>ServerTime</c> it is handed since WL-92, so a full pull is a first sync, not every restart;
    /// it sends <c>DateTime.MinValue</c> explicitly rather than omitting the parameter.)
    /// AssumeUniversal covers a client that sends no offset; AdjustToUniversal normalizes one that
    /// does.
    /// </summary>
    public static bool TryParseSince(string? raw, out DateTime since)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            since = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
            return true;
        }

        if (DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            since = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        since = default;
        return false;
    }
}
