namespace WorkLedger.Contracts.Auth;

/// <summary>DeviceId is the client's per-install GUID string (see Sync.SyncChangeDto.OriginDeviceId).</summary>
public sealed record RegisterDeviceRequest(string DeviceId, string? Label);

public sealed record RegisterDeviceResponse(string Token, DateTime ExpiresAtUtc);

public sealed record WhoAmIResponse(int AccountId, string DeviceId);
