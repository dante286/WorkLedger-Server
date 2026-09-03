using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Payloads;
using WorkLedger.Contracts.Sync;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Sync;

/// <summary>
/// Everything the reconciliation loop needs to know about one synced entity type. The loop itself
/// (SyncService) is entity-agnostic: all the per-type knowledge — which DbSet, which payload DTO,
/// which columns — lives behind this interface, so the last-write-wins rules exist in exactly one
/// place and cannot drift between projects, tasks, and time entries.
/// </summary>
internal interface ISyncEntityHandler
{
    string EntityType { get; }

    /// <summary>
    /// Dependency rank within a push batch: projects before tasks before time entries, so a parent
    /// pushed in the same batch as its child is already present when the child lands.
    /// </summary>
    int OrderRank { get; }

    Task<IVersionedSyncEntity?> FindAsync(WorkLedgerDbContext db, int userId, Guid syncId, CancellationToken cancellationToken);

    Task<List<IVersionedSyncEntity>> QueryChangedAsync(
        WorkLedgerDbContext db, int userId, DateTime since, string callerDeviceId, CancellationToken cancellationToken);

    IVersionedSyncEntity NewRow(int userId, Guid syncId);

    void Add(WorkLedgerDbContext db, IVersionedSyncEntity entity);

    /// <summary>
    /// Copies the payload's data columns onto the row. Returns an error string if the payload is
    /// unusable, or null on success.
    /// <para>
    /// <paramref name="required"/> is false for deletes: the client sends the entity as the payload
    /// of a delete, but a tombstone is meaningful without it, so a missing or unusable payload on a
    /// delete is tolerated (the row keeps the columns it already had) rather than failing the item.
    /// Losing a delete is far worse than losing the data of a row that is about to be tombstoned.
    /// </para>
    /// </summary>
    string? ApplyPayload(IVersionedSyncEntity entity, string? payloadJson, bool required);

    string SerializePayload(IVersionedSyncEntity entity);
}

/// <summary>
/// The one implementation, closed over a concrete (entity, payload) pair by the registrations in
/// <see cref="SyncEntityHandlers"/>. The queries are supplied as delegates over the concrete DbSet
/// rather than written generically here, so every user-scoped WHERE clause is visible at the
/// registration site — this is the tenancy boundary, and it should be readable without chasing
/// generics.
/// </summary>
internal sealed class SyncEntityHandler<TEntity, TPayload>(
    string entityType,
    int orderRank,
    Func<WorkLedgerDbContext, int, Guid, CancellationToken, Task<TEntity?>> find,
    Func<WorkLedgerDbContext, int, DateTime, string, IQueryable<TEntity>> queryChanged,
    Action<TEntity, TPayload> apply,
    Func<TEntity, TPayload> project,
    Func<TPayload, string?>? validate = null) : ISyncEntityHandler
    where TEntity : class, IVersionedSyncEntity, new()
    where TPayload : class
{
    public string EntityType { get; } = entityType;

    public int OrderRank { get; } = orderRank;

    public async Task<IVersionedSyncEntity?> FindAsync(
        WorkLedgerDbContext db, int userId, Guid syncId, CancellationToken cancellationToken)
        => await find(db, userId, syncId, cancellationToken);

    public async Task<List<IVersionedSyncEntity>> QueryChangedAsync(
        WorkLedgerDbContext db, int userId, DateTime since, string callerDeviceId, CancellationToken cancellationToken)
    {
        var rows = await queryChanged(db, userId, since, callerDeviceId)
            .OrderBy(e => e.UpdatedAt)
            .ToListAsync(cancellationToken);
        return [.. rows.Cast<IVersionedSyncEntity>()];
    }

    public IVersionedSyncEntity NewRow(int userId, Guid syncId)
        => new TEntity { UserId = userId, SyncId = syncId };

    public void Add(WorkLedgerDbContext db, IVersionedSyncEntity entity) => db.Add((TEntity)entity);

    public string? ApplyPayload(IVersionedSyncEntity entity, string? payloadJson, bool required)
    {
        TPayload? payload;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return required ? "PayloadJson is required for a create/update." : null;
        }

        try
        {
            payload = JsonSerializer.Deserialize<TPayload>(payloadJson, SyncJson.Options);
        }
        catch (JsonException ex)
        {
            return required ? $"PayloadJson is not valid {typeof(TPayload).Name} JSON: {ex.Message}" : null;
        }

        if (payload is null)
        {
            return required ? "PayloadJson deserialized to null." : null;
        }

        var error = validate?.Invoke(payload);
        if (error is not null)
        {
            // On the best-effort (delete) path a payload that fails validation is skipped rather
            // than applied — the row's existing columns are more trustworthy than a bad payload.
            return required ? error : null;
        }

        apply((TEntity)entity, payload);
        return null;
    }

    public string SerializePayload(IVersionedSyncEntity entity)
        => JsonSerializer.Serialize(project((TEntity)entity), SyncJson.Options);
}

/// <summary>The registry the reconciliation loop dispatches through, keyed by wire entity type.</summary>
internal static class SyncEntityHandlers
{
    public static readonly IReadOnlyDictionary<string, ISyncEntityHandler> ByEntityType =
        new Dictionary<string, ISyncEntityHandler>(StringComparer.OrdinalIgnoreCase)
        {
            [SyncEntityTypes.Project] = new SyncEntityHandler<Project, ProjectPayload>(
                SyncEntityTypes.Project,
                orderRank: 0,
                find: (db, userId, syncId, ct) =>
                    db.Projects.SingleOrDefaultAsync(p => p.UserId == userId && p.SyncId == syncId, ct),
                queryChanged: (db, userId, since, callerDeviceId) =>
                    db.Projects.Where(p =>
                        p.UserId == userId &&
                        p.UpdatedAt > since &&
                        (p.OriginDeviceId == null || p.OriginDeviceId != callerDeviceId)),
                apply: (entity, payload) =>
                {
                    entity.Name = payload.Name;
                    entity.ProjectCode = payload.ProjectCode;
                    entity.Description = payload.Description;
                    entity.CreatedAt = SyncTime.ToUtc(payload.CreatedAt);
                },
                project: entity => new ProjectPayload(
                    entity.SyncId,
                    entity.Name,
                    entity.ProjectCode,
                    entity.Description,
                    entity.CreatedAt,
                    entity.UpdatedAt,
                    entity.Version,
                    entity.OriginDeviceId)),

            [SyncEntityTypes.Task] = new SyncEntityHandler<TaskItem, TaskItemPayload>(
                SyncEntityTypes.Task,
                orderRank: 1,
                find: (db, userId, syncId, ct) =>
                    db.TaskItems.SingleOrDefaultAsync(t => t.UserId == userId && t.SyncId == syncId, ct),
                queryChanged: (db, userId, since, callerDeviceId) =>
                    db.TaskItems.Where(t =>
                        t.UserId == userId &&
                        t.UpdatedAt > since &&
                        (t.OriginDeviceId == null || t.OriginDeviceId != callerDeviceId)),
                apply: (entity, payload) =>
                {
                    entity.ProjectSyncId = payload.ProjectSyncId;
                    entity.Name = payload.Name;
                    entity.Notes = payload.Notes;
                    entity.IsCompleted = payload.IsCompleted;
                    entity.IsArchived = payload.IsArchived;
                    entity.CreatedAt = SyncTime.ToUtc(payload.CreatedAt);
                },
                project: entity => new TaskItemPayload(
                    entity.SyncId,
                    entity.ProjectSyncId,
                    entity.Name,
                    entity.Notes,
                    entity.IsCompleted,
                    entity.IsArchived,
                    entity.CreatedAt,
                    entity.UpdatedAt,
                    entity.Version,
                    entity.OriginDeviceId),
                validate: payload => payload.ProjectSyncId == Guid.Empty
                    ? "ProjectSyncId is required and must not be Guid.Empty. The client does not yet carry a " +
                      "parent SyncId on TaskItem (it has only the local int ProjectId) — see WL-91. Storing the " +
                      "row anyway would make it an orphan that no report can attribute."
                    : null),

            [SyncEntityTypes.TimeEntry] = new SyncEntityHandler<TimeEntry, TimeEntryPayload>(
                SyncEntityTypes.TimeEntry,
                orderRank: 2,
                find: (db, userId, syncId, ct) =>
                    db.TimeEntries.SingleOrDefaultAsync(t => t.UserId == userId && t.SyncId == syncId, ct),
                queryChanged: (db, userId, since, callerDeviceId) =>
                    db.TimeEntries.Where(t =>
                        t.UserId == userId &&
                        t.UpdatedAt > since &&
                        (t.OriginDeviceId == null || t.OriginDeviceId != callerDeviceId)),
                apply: (entity, payload) =>
                {
                    entity.TaskSyncId = payload.TaskSyncId;
                    entity.StartTime = SyncTime.ToUtc(payload.StartTime);
                    entity.EndTime = SyncTime.ToUtc(payload.EndTime);
                    entity.DurationSeconds = payload.DurationSeconds;
                    entity.CreatedAt = SyncTime.ToUtc(payload.CreatedAt);
                },
                project: entity => new TimeEntryPayload(
                    entity.SyncId,
                    entity.TaskSyncId,
                    entity.StartTime,
                    entity.EndTime,
                    entity.DurationSeconds,
                    entity.CreatedAt,
                    entity.UpdatedAt,
                    entity.Version,
                    entity.OriginDeviceId),
                validate: payload => payload.TaskSyncId == Guid.Empty
                    ? "TaskSyncId is required and must not be Guid.Empty. The client does not yet carry a parent " +
                      "SyncId on TimeEntry (it has only the local int TaskItemId) — see WL-91. Storing the row " +
                      "anyway would make it an orphan that no report can attribute."
                    : null),
        };
}
