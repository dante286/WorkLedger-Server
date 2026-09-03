namespace WorkLedger.Server.Data.Entities;

public sealed class Device
{
    public int Id { get; set; }
    public int UserId { get; set; }

    /// <summary>The client's per-install GUID string (see client SyncQueue/RemoteChange OriginDeviceId).</summary>
    public string DeviceId { get; set; } = string.Empty;

    public string? Label { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenUtc { get; set; }
}
