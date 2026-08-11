namespace WorkLedger.Contracts.Payloads;

// Reconciled against WorkLedger-Client's Models/Project.cs, Models/TaskItem.cs, and Models/TimeEntry.cs
// (client repo, checked 2026-08-11). Parent links use the client's cross-device SyncId, not its local
// autoincrement int (ProjectId / TaskItemId) — see plans/S0 and the client's IVersionedEntity.cs.

public sealed record ProjectPayload(
    Guid SyncId,
    string? Name,
    string? ProjectCode,
    string? Description,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    int Version,
    string? OriginDeviceId);

public sealed record TaskItemPayload(
    Guid SyncId,
    Guid ProjectSyncId,
    string? Name,
    string? Notes,
    bool IsCompleted,
    bool IsArchived,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    int Version,
    string? OriginDeviceId);

public sealed record TimeEntryPayload(
    Guid SyncId,
    Guid TaskSyncId,
    DateTime StartTime,
    DateTime? EndTime,
    long? DurationSeconds,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    int Version,
    string? OriginDeviceId);
