namespace WorkLedger.Contracts.Sync;

/// <summary>
/// One create/update/delete event for one entity. EntityId is the entity's cross-device
/// SyncId (Guid), NOT any local autoincrement Id — keying off a local int silently collides
/// across devices.
/// </summary>
public sealed record SyncChangeDto(
    string EntityType,
    Guid EntityId,
    string Operation,
    string? PayloadJson,
    int Version,
    string OriginDeviceId,
    DateTime CreatedAt);

public sealed record SyncPushRequest(IReadOnlyList<SyncChangeDto> Changes);

/// <summary>
/// Per-item outcome so the client can reconcile immediately rather than waiting for the next pull.
/// </summary>
/// <param name="AuthoritativePayloadJson">
/// The winning record, present only when the server's own row beat the pushed change. A validation
/// failure leaves this null — that is how a client tells "my row won, here it is" apart from "your
/// change was malformed".
/// </param>
/// <param name="AuthoritativeOperation">
/// What <paramref name="AuthoritativePayloadJson"/> represents: <c>"update"</c> for a live row,
/// <c>"delete"</c> for a tombstone. Null whenever there is no payload.
/// <para>
/// Without this the payload is not self-describing, and a client adopting it has to guess the
/// operation. The only available guess is "update", which resurrects a deleted entity locally and
/// permanently (a three-device conflict reaches this: C deletes, B pushes a stale update, the
/// tombstone wins and is handed back). The next pull does not rescue it either — a losing item
/// performs no write, so the winner's UpdatedAt is already behind the client's cursor. See WL-98.
/// </para>
/// Appended with a default so adding it is source- and wire-compatible for existing clients.
/// </param>
public sealed record SyncItemResult(
    Guid EntityId,
    bool Accepted,
    int AuthoritativeVersion,
    string? AuthoritativePayloadJson,
    string? Reason,
    string? AuthoritativeOperation = null);

public sealed record SyncPushResponse(
    IReadOnlyList<SyncItemResult> Results,
    DateTime ServerTime);

public sealed record SyncPullResponse(
    IReadOnlyList<SyncChangeDto> Changes,
    DateTime ServerTime);
