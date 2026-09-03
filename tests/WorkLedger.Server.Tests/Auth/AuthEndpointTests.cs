using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
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
}
