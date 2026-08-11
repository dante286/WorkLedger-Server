namespace WorkLedger.Server.Data.Entities;

/// <summary>
/// Shared shape for entities that participate in cross-device sync (S3 merge logic binds to
/// this, not to any specific entity type). Mirrors the client's IVersionedEntity, plus the
/// server-only IsDeleted tombstone and UpdatedAt pull cursor.
/// </summary>
public interface IVersionedSyncEntity
{
    Guid SyncId { get; set; }
    int Version { get; set; }
    string? OriginDeviceId { get; set; }
    bool IsDeleted { get; set; }

    /// <summary>Server-stamped on every write by WorkLedgerDbContext — never trust a client clock for this.</summary>
    DateTime UpdatedAt { get; set; }
}
