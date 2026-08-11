namespace WorkLedger.Server.Data.Entities;

public sealed class Project : IVersionedSyncEntity
{
    public int Id { get; set; }
    public int AccountId { get; set; }

    public Guid SyncId { get; set; }
    public int Version { get; set; }
    public string? OriginDeviceId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string? Name { get; set; }
    public string? ProjectCode { get; set; }
    public string? Description { get; set; }
}
