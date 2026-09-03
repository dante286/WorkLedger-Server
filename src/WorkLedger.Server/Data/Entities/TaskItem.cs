namespace WorkLedger.Server.Data.Entities;

public sealed class TaskItem : IVersionedSyncEntity
{
    public int Id { get; set; }
    public int UserId { get; set; }

    public Guid SyncId { get; set; }

    /// <summary>Parent link by cross-device SyncId, never the client's local int ProjectId.</summary>
    public Guid ProjectSyncId { get; set; }

    public int Version { get; set; }
    public string? OriginDeviceId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string? Name { get; set; }
    public string? Notes { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsArchived { get; set; }
}
