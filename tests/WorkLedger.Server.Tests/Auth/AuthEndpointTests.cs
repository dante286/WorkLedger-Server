using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Auth;

namespace WorkLedger.Server.Tests.Auth;

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
    public async Task Health_IsReachableWithoutToken()
    {
        var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RegisterDevice_WithCorrectSecret_ReturnsSignedToken()
    {
        var client = fixture.Factory.CreateClient();

        var token = await RegisterDeviceAsync(client, Guid.NewGuid().ToString());

        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public async Task RegisterDevice_WithWrongSecret_Returns401()
    {
        var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/register-device")
        {
            Content = JsonContent.Create(new { deviceId = Guid.NewGuid().ToString() }),
        };
        request.Headers.Add("X-Bootstrap-Secret", "definitely-wrong-secret");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegisterDevice_WithMissingSecret_Returns401()
    {
        var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/register-device", new { deviceId = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IssuedToken_ResolvesToCorrectAccountAndDevice()
    {
        var client = fixture.Factory.CreateClient();
        var deviceId = Guid.NewGuid().ToString();
        var token = await RegisterDeviceAsync(client, deviceId);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WhoAmIResponse>();
        Assert.Equal(deviceId, body!.DeviceId);
        Assert.True(body.AccountId > 0);
    }

    [Fact]
    public async Task RegisterDevice_ReRegisteringSameDeviceId_IsIdempotent()
    {
        var client = fixture.Factory.CreateClient();
        var deviceId = Guid.NewGuid().ToString();

        await RegisterDeviceAsync(client, deviceId, label: "First");
        var secondToken = await RegisterDeviceAsync(client, deviceId, label: "Renamed");

        using var context = fixture.CreateDbContext();
        var matches = await context.Devices.Where(d => d.DeviceId == deviceId).ToListAsync();
        Assert.Single(matches);
        Assert.Equal("Renamed", matches[0].Label);

        using var whoamiRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/whoami");
        whoamiRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secondToken);
        var whoamiResponse = await client.SendAsync(whoamiRequest);

        Assert.Equal(HttpStatusCode.OK, whoamiResponse.StatusCode);
    }

    private async Task<string> RegisterDeviceAsync(HttpClient client, string deviceId, string? label = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/register-device")
        {
            Content = JsonContent.Create(new { deviceId, label }),
        };
        request.Headers.Add("X-Bootstrap-Secret", fixture.BootstrapSecret);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<RegisterDeviceResponse>();
        return body!.Token;
    }
}
