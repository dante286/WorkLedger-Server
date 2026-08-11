namespace WorkLedger.Contracts.Payloads;

// PLACEHOLDER SHAPES — not yet reconciled against the client's Models/*.cs (see plans/S0, "Flag if
// unclear"). Field lists here must be confirmed against WorkLedger-Client's Project.cs/TaskItem.cs/
// TimeEntry.cs before S1/S3 depend on them.

public sealed record ProjectPayload(
    Guid SyncId,
    string? Name,
    string? ProjectCode,
    string? Description,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    int Version,
    string? OriginDeviceId,
    bool IsArchived);

public sealed record TaskItemPayload(
    Guid SyncId,
    Guid ProjectSyncId,
    string? Name,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    int Version,
    string? OriginDeviceId,
    bool IsArchived);

public sealed record TimeEntryPayload(
    Guid SyncId,
    Guid TaskSyncId,
    DateTime StartUtc,
    DateTime? EndUtc,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    int Version,
    string? OriginDeviceId);
