namespace WorkLedger.Server.Auth;

public sealed record JwtOptions(string SigningKey, string Issuer, string Audience, int ExpiryDays);

public sealed record BootstrapOptions(string Secret);
