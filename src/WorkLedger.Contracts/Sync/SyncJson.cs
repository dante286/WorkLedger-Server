using System.Text.Json;

namespace WorkLedger.Contracts.Sync;

/// <summary>
/// The single JSON policy for <see cref="SyncChangeDto.PayloadJson"/> on both sides of the wire.
/// <para>
/// <b>PascalCase out</b> (the default naming policy, deliberately not overridden): the client reads
/// payloads with a bare <c>JsonSerializer.Deserialize&lt;T&gt;(payloadJson)</c> and default,
/// case-<i>sensitive</i> options. Emitting camelCase — which is what
/// <see cref="JsonSerializerDefaults.Web"/> would do — makes every property miss, so the payload
/// deserializes into an all-defaults entity <i>without throwing</i>. That is silent data loss, not a
/// style preference, so do not "modernize" this to web defaults.
/// </para>
/// <para>
/// <b>Case-insensitive in</b>: costs nothing and means a client that does switch to camelCase is
/// merely non-canonical rather than broken.
/// </para>
/// This governs the payload string only. The surrounding envelope (SyncPushRequest and friends) is
/// serialized by ASP.NET Core's own web defaults, which are case-insensitive on read, so the two
/// casings coexist safely.
/// </summary>
public static class SyncJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
