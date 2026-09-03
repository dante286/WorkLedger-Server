namespace WorkLedger.Server.Data.Entities;

public sealed class TimeEntry : IVersionedSyncEntity
{
    public int Id { get; set; }
    public int UserId { get; set; }

    public Guid SyncId { get; set; }

    /// <summary>Parent link by cross-device SyncId, never the client's local int TaskItemId.</summary>
    public Guid TaskSyncId { get; set; }

    public int Version { get; set; }
    public string? OriginDeviceId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public long? DurationSeconds { get; set; }
}
