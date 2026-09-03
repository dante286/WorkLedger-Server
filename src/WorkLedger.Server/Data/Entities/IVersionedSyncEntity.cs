namespace WorkLedger.Server.Data.Entities;

/// <summary>
/// Shared shape for entities that participate in cross-device sync (S3 merge logic binds to
/// this, not to any specific entity type). Mirrors the client's IVersionedEntity, plus the
/// server-only IsDeleted tombstone and UpdatedAt pull cursor.
/// </summary>
public interface IVersionedSyncEntity
{
    /// <summary>The tenancy boundary. Every sync query filters on this; forgetting to is a data leak.</summary>
    int UserId { get; set; }

    Guid SyncId { get; set; }
    int Version { get; set; }
    string? OriginDeviceId { get; set; }
    bool IsDeleted { get; set; }

    /// <summary>The entity's own creation time, as reported by the device that created it.</summary>
    DateTime CreatedAt { get; set; }

    /// <summary>Server-stamped on every write by WorkLedgerDbContext — never trust a client clock for this.</summary>
    DateTime UpdatedAt { get; set; }
}
