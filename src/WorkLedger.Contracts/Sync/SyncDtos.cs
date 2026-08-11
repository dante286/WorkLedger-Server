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
public sealed record SyncItemResult(
    Guid EntityId,
    bool Accepted,
    int AuthoritativeVersion,
    string? AuthoritativePayloadJson,
    string? Reason);

public sealed record SyncPushResponse(
    IReadOnlyList<SyncItemResult> Results,
    DateTime ServerTime);

public sealed record SyncPullResponse(
    IReadOnlyList<SyncChangeDto> Changes,
    DateTime ServerTime);
