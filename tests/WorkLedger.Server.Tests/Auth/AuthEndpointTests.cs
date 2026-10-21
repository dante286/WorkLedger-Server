using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WorkLedger.Contracts.Auth;

namespace WorkLedger.Server.Tests.Auth;

/// <summary>
/// The token pipeline: the fallback authorization policy, and a token resolving to the right
/// <c>(user_id, device_id)</c> pair.
/// <para>
/// Token <i>issuing</i> lives in <see cref="LoginEndpointTests"/>, against a private database — it
/// depends on credentials and on how many users the whole database holds, and this collection's
/// database is shared. What is left here is the plumbing every other suite's tokens ride on.
/// </para>
/// </summary>
[Collection(nameof(AuthCollection))]
public class AuthEndpointTests(AuthWebAppFixture fixture)
{
    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/auth/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithATokenSignedByTheWrongKey_Returns401()
    {
        var foreign = new Server.Auth.JwtTokenService(new Server.Auth.JwtOptions(
            "a-completely-different-signing-key-0123456789", TestConfig.JwtIssuer, TestConfig.JwtAudience, 1));
        var (token, _) = foreign.IssueToken(1, 1, "impostor", Guid.NewGuid().ToString());

        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetAsync("/auth/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Everything about this token is right — the real key, a real device, the stored stamp — except
    /// that it expired a day ago, well outside the validator's one-minute clock skew. The device's own
    /// unexpired token is checked first, so the 401 can only be the expiry.
    /// </summary>
    [Fact]
    public async Task ProtectedEndpoint_WithAnExpiredToken_Returns401WithNoBody()
    {
        var userId = await fixture.NewUserAsync("expired");
        var device = await fixture.EnrollDeviceAsync(userId, "expired-test");
        Assert.Equal(HttpStatusCode.OK, (await device.Client.GetAsync("/auth/whoami")).StatusCode);

        using var db = fixture.CreateDbContext();
        var deviceRow = await db.Devices.SingleAsync(d => d.UserId == userId && d.DeviceId == device.DeviceId);
        var stamp = await db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync();
        var now = DateTime.UtcNow;
        var expired = MintToken(userId, deviceRow.Id, device.DeviceId, stamp,
            notBefore: now.AddDays(-2), expires: now.AddDays(-1));

        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expired);
        var response = await client.GetAsync("/auth/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A bearer value that is not a JWT at all fails as a plain 401 — not a 500, and not a body carrying
    /// the parser's exception.
    /// </summary>
    [Fact]
    public async Task ProtectedEndpoint_WithANonJwtBearerValue_Returns401WithNoBody()
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        var response = await client.GetAsync("/auth/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_IsReachableWithoutToken()
    {
        var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task IssuedToken_ResolvesToCorrectUserAndDevice()
    {
        var userId = await fixture.NewUserAsync("whoami");
        var device = await fixture.EnrollDeviceAsync(userId, "whoami-test");

        var response = await device.Client.GetAsync("/auth/whoami");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WhoAmIResponse>();
        Assert.Equal(device.DeviceId, body!.DeviceId);
        Assert.Equal(userId, body.UserId);
    }

    /// <summary>
    /// Two devices under one user get distinct <c>device_id</c> claims but the same <c>user_id</c> —
    /// the shape the whole multi-device tenancy rests on.
    /// </summary>
    [Fact]
    public async Task TwoDevicesUnderOneUser_ShareTheUserButNotTheDeviceIdentity()
    {
        var userId = await fixture.NewUserAsync("pair");
        var first = await fixture.EnrollDeviceAsync(userId, "first");
        var second = await fixture.EnrollDeviceAsync(userId, "second");

        var firstWhoAmI = await first.Client.GetFromJsonAsync<WhoAmIResponse>("/auth/whoami");
        var secondWhoAmI = await second.Client.GetFromJsonAsync<WhoAmIResponse>("/auth/whoami");

        Assert.Equal(userId, firstWhoAmI!.UserId);
        Assert.Equal(userId, secondWhoAmI!.UserId);
        Assert.NotEqual(firstWhoAmI.DeviceId, secondWhoAmI.DeviceId);

        using var db = fixture.CreateDbContext();
        Assert.Equal(2, await db.Devices.CountAsync(d => d.UserId == userId));
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// What <c>JwtTokenService.IssueToken</c> produces, signed with the host's own key, with the lifetime the caller chooses — the
    /// service computes its own from <c>Jwt:ExpiryDays</c> and cannot be asked for one already over.
    /// The claim names are the service's; <c>stamp</c> is spelled out because the constant lives on
    /// the internal <c>TokenRevocation</c>.
    /// </summary>
    private static string MintToken(
        int userId, int deviceRowId, string deviceId, string securityStamp,
        DateTime notBefore, DateTime expires)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestConfig.JwtSigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: TestConfig.JwtIssuer,
            audience: TestConfig.JwtAudience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, deviceRowId.ToString()),
                new Claim("user_id", userId.ToString()),
                new Claim("device_id", deviceId),
                new Claim("stamp", securityStamp),
            ],
            notBefore: notBefore,
            expires: expires,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
