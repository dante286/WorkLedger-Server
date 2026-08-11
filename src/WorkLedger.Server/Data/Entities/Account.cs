namespace WorkLedger.Server.Data.Entities;

/// <summary>v1 is single-account, multi-device — exactly one row exists, provisioned in S2.</summary>
public sealed class Account
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
