using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Tests.Auth;

/// <summary>
/// <c>POST /auth/register</c>, <c>POST /auth/claim</c> and <c>GET /auth/capabilities</c> — every S6
/// behaviour that is an answer about how many users the database holds. See
/// <see cref="UserAccountsFixture"/> for why they need a database of their own.
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public class RegistrationEndpointTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string GoodPassword = "correct horse battery staple";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Register_WithCorrectSecret_Creates201AndPersistsANormalizedEmailAndAHash()
    {
        using var host = fixture.CreateHost();
        var client = host.CreateClient();

        var response = await PostAsync(client, "/auth/register", "Sam@Example.COM", GoodPassword, "Sam", fixture.BootstrapSecret);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserSummary>();
        Assert.Equal("Sam@Example.COM", body!.Email);
        Assert.Equal("Sam", body.DisplayName);
        Assert.True(body.Id > 0);

        using var db = fixture.CreateDbContext();
        var user = await db.Users.SingleAsync();
        Assert.Equal("Sam@Example.COM", user.Email);
        Assert.Equal("SAM@EXAMPLE.COM", user.NormalizedEmail);
        Assert.NotNull(user.PasswordHash);
        Assert.NotEqual(GoodPassword, user.PasswordHash);
        Assert.DoesNotContain(GoodPassword, user.PasswordHash);
        Assert.False(string.IsNullOrWhiteSpace(user.SecurityStamp));
    }

    /// <summary>Registration mints no token — tokens are device-scoped and belong to login (S7).</summary>
    [Fact]
    public async Task Register_ReturnsNoToken()
    {
        using var host = fixture.CreateHost();

        var response = await PostAsync(host.CreateClient(), "/auth/register", "sam@example.com", GoodPassword, null, fixture.BootstrapSecret);

        var payload = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("token", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_DuplicateEmailDifferingOnlyInCase_Returns409()
    {
        using var host = fixture.CreateHost();
        var client = host.CreateClient();
        await PostAsync(client, "/auth/register", "sam@example.com", GoodPassword, null, fixture.BootstrapSecret);

        var response = await PostAsync(client, "/auth/register", "SAM@EXAMPLE.COM", GoodPassword, null, fixture.BootstrapSecret);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Register_WithShortPassword_Returns400()
    {
        using var host = fixture.CreateHost();

        var response = await PostAsync(host.CreateClient(), "/auth/register", "sam@example.com", "short", null, fixture.BootstrapSecret);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoUsers();
    }

    /// <summary>The default floor is 12, so an 11-character password is rejected and a 12 is not.</summary>
    [Theory]
    [InlineData("elevenchars", HttpStatusCode.BadRequest)]
    [InlineData("twelvechars!", HttpStatusCode.Created)]
    public async Task Register_EnforcesTheDefaultMinimumPasswordLength(string password, HttpStatusCode expected)
    {
        using var host = fixture.CreateHost();

        var response = await PostAsync(host.CreateClient(), "/auth/register", "sam@example.com", password, null, fixture.BootstrapSecret);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Register_HonoursAnOverriddenMinimumPasswordLength()
    {
        using var host = fixture.CreateHost(("Auth:MinPasswordLength", "30"));

        var response = await PostAsync(host.CreateClient(), "/auth/register", "sam@example.com", GoodPassword[..20], null, fixture.BootstrapSecret);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@nolocalpart.com")]
    [InlineData("nodomain@")]
    [InlineData("two@at@signs.com")]
    public async Task Register_WithMalformedEmail_Returns400(string email)
    {
        using var host = fixture.CreateHost();

        var response = await PostAsync(host.CreateClient(), "/auth/register", email, GoodPassword, null, fixture.BootstrapSecret);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoUsers();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("definitely-wrong-secret")]
    public async Task Register_WithWrongOrMissingSecret_Returns401(string? secret)
    {
        using var host = fixture.CreateHost();

        var response = await PostAsync(host.CreateClient(), "/auth/register", "sam@example.com", GoodPassword, null, secret);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoUsers();
    }

    /// <summary>
    /// 403 and not 404: <c>/auth/capabilities</c> already advertises the flag, so hiding the route would
    /// buy no secrecy and only cost an operator a confusing debugging session.
    /// </summary>
    [Fact]
    public async Task Register_WhenRegistrationIsDisabled_Returns403BeforeCheckingTheSecret()
    {
        using var host = fixture.CreateHost(("Auth:RegistrationEnabled", "false"));

        var response = await PostAsync(host.CreateClient(), "/auth/register", "sam@example.com", GoodPassword, null, secret: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoUsers();
    }

    /// <summary>The flat alias operators reach for, which the colon-keyed setting overrides.</summary>
    [Fact]
    public async Task Register_HonoursTheRegistrationsAllowedAlias()
    {
        using var host = fixture.CreateHost(("REGISTRATIONS_ALLOWED", "false"));

        var response = await PostAsync(host.CreateClient(), "/auth/register", "sam@example.com", GoodPassword, null, fixture.BootstrapSecret);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Register_CanonicalKeyBeatsTheAlias()
    {
        using var host = fixture.CreateHost(
            ("REGISTRATIONS_ALLOWED", "false"),
            ("Auth:RegistrationEnabled", "true"));

        var response = await PostAsync(host.CreateClient(), "/auth/register", "sam@example.com", GoodPassword, null, fixture.BootstrapSecret);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// The upgrade path: the legacy row keeps its <c>Id</c>, so everything hanging off it — here a
    /// project and a device — is still reachable under the now-credentialed user.
    /// </summary>
    [Fact]
    public async Task Claim_AgainstTheLegacyUser_KeepsItsIdAndItsData()
    {
        var (legacyUserId, projectSyncId) = await SeedLegacyTenantAsync();
        using var host = fixture.CreateHost();

        var response = await PostAsync(host.CreateClient(), "/auth/claim", "owner@example.com", GoodPassword, "Owner", fixture.BootstrapSecret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserSummary>();
        Assert.Equal(legacyUserId, body!.Id);
        Assert.Equal("owner@example.com", body.Email);

        using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync());
        var user = await db.Users.SingleAsync();
        Assert.Equal(legacyUserId, user.Id);
        Assert.Equal("OWNER@EXAMPLE.COM", user.NormalizedEmail);
        Assert.NotNull(user.PasswordHash);
        Assert.NotNull(user.UpdatedAt);
        Assert.True(await db.Projects.AnyAsync(p => p.UserId == legacyUserId && p.SyncId == projectSyncId));
        Assert.True(await db.Devices.AnyAsync(d => d.UserId == legacyUserId));
    }

    [Fact]
    public async Task Claim_ASecondTime_Returns409()
    {
        await SeedLegacyTenantAsync();
        using var host = fixture.CreateHost();
        var client = host.CreateClient();
        await PostAsync(client, "/auth/claim", "owner@example.com", GoodPassword, null, fixture.BootstrapSecret);

        var response = await PostAsync(client, "/auth/claim", "someone@example.com", GoodPassword, null, fixture.BootstrapSecret);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Claim_OnAFreshDatabase_Returns409()
    {
        using var host = fixture.CreateHost();

        var response = await PostAsync(host.CreateClient(), "/auth/claim", "owner@example.com", GoodPassword, null, fixture.BootstrapSecret);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Claim_WithWrongSecret_Returns401()
    {
        await SeedLegacyTenantAsync();
        using var host = fixture.CreateHost();

        var response = await PostAsync(host.CreateClient(), "/auth/claim", "owner@example.com", GoodPassword, null, "wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// An operator who switched registration off must still be able to claim their own existing data.
    /// </summary>
    [Fact]
    public async Task Claim_IgnoresTheRegistrationFlag()
    {
        await SeedLegacyTenantAsync();
        using var host = fixture.CreateHost(("Auth:RegistrationEnabled", "false"));

        var response = await PostAsync(host.CreateClient(), "/auth/claim", "owner@example.com", GoodPassword, null, fixture.BootstrapSecret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task Capabilities_ReflectsTheRegistrationFlagInBothPositions(string configured, bool expected)
    {
        using var host = fixture.CreateHost(("Auth:RegistrationEnabled", configured));

        var capabilities = await GetCapabilitiesAsync(host);

        Assert.Equal(expected, capabilities.RegistrationEnabled);
        Assert.True(capabilities.RegistrationRequiresSecret);
    }

    /// <summary>
    /// The assertion that catches the lockout numbers being hardcoded into the response rather than read
    /// from configuration — the whole reason S6 defines the fields it does not yet enforce (WL-147).
    /// </summary>
    [Fact]
    public async Task Capabilities_ReportsTheDefaultLockoutPolicy()
    {
        using var host = fixture.CreateHost();

        var capabilities = await GetCapabilitiesAsync(host);

        Assert.Equal(12, capabilities.MinPasswordLength);
        Assert.Equal(5, capabilities.MaxFailedLogins);
        Assert.Equal(15, capabilities.LockoutMinutes);
    }

    [Fact]
    public async Task Capabilities_ReportsAnOverriddenLockoutPolicy()
    {
        using var host = fixture.CreateHost(
            ("Auth:MinPasswordLength", "20"),
            ("Auth:MaxFailedLogins", "3"),
            ("Auth:LockoutMinutes", "60"));

        var capabilities = await GetCapabilitiesAsync(host);

        Assert.Equal(20, capabilities.MinPasswordLength);
        Assert.Equal(3, capabilities.MaxFailedLogins);
        Assert.Equal(60, capabilities.LockoutMinutes);
    }

    /// <summary>
    /// The lockout window binds fractionally so the S7 suite can wait one out (see
    /// <c>AuthOptions.LockoutWindow</c>), but the wire field is whole minutes and rounds <b>up</b> — a
    /// three-second window must not advertise as "try again in 0 minutes".
    /// </summary>
    [Fact]
    public async Task Capabilities_RoundsASubMinuteLockoutWindowUp()
    {
        using var host = fixture.CreateHost(("Auth:LockoutMinutes", "0.05"));

        var capabilities = await GetCapabilitiesAsync(host);

        Assert.Equal(1, capabilities.LockoutMinutes);
    }

    [Fact]
    public async Task Capabilities_HasUnclaimedAccount_TracksTheLegacyRow()
    {
        using var host = fixture.CreateHost();
        Assert.False((await GetCapabilitiesAsync(host)).HasUnclaimedAccount);

        await SeedLegacyTenantAsync();
        Assert.True((await GetCapabilitiesAsync(host)).HasUnclaimedAccount);

        await PostAsync(host.CreateClient(), "/auth/claim", "owner@example.com", GoodPassword, null, fixture.BootstrapSecret);
        Assert.False((await GetCapabilitiesAsync(host)).HasUnclaimedAccount);
    }

    [Fact]
    public async Task Capabilities_NeedsNoToken()
    {
        using var host = fixture.CreateHost();

        var response = await host.CreateClient().GetAsync("/auth/capabilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The endpoint is gone, not merely deprecated: S7 replaced it with <c>/auth/login</c>, and its two
    /// DTOs left <c>WorkLedger.Contracts</c> with it. Asserted rather than assumed, because a shared
    /// secret that could still mint a token would quietly undo the security improvement of removing it.
    /// <para>
    /// Set up as the exact case S6 answered with <c>200</c> and a token — the right secret, exactly one
    /// user — so a passing assertion means the route is really unreachable rather than merely fenced.
    /// It comes back <c>401</c> rather than <c>404</c> because Program.cs's authorization fallback policy
    /// covers requests that match no endpoint at all, which is this app's answer for every unknown path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RegisterDevice_IsGone_AndTheBootstrapSecretCanNoLongerMintAToken()
    {
        using var host = fixture.CreateHost();
        var client = host.CreateClient();
        await PostAsync(client, "/auth/register", "first@example.com", GoodPassword, null, fixture.BootstrapSecret);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/register-device")
        {
            Content = JsonContent.Create(new { deviceId = Guid.NewGuid().ToString(), label = "gone" }),
        };
        request.Headers.Add("X-Bootstrap-Secret", fixture.BootstrapSecret);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("token", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var db = fixture.CreateDbContext();
        Assert.Equal(0, await db.Devices.CountAsync());
    }

    private async Task AssertNoUsers()
    {
        using var db = fixture.CreateDbContext();
        Assert.Equal(0, await db.Users.CountAsync());
    }

    /// <summary>
    /// A pre-S6 deployment as the migration leaves it: one user with no credentials, one enrolled
    /// device, and one project hanging off it. Returns the user's id and the project's SyncId so a claim
    /// can be checked for having kept both.
    /// </summary>
    private async Task<(int UserId, Guid ProjectSyncId)> SeedLegacyTenantAsync()
    {
        using var db = fixture.CreateDbContext();
        var now = DateTime.UtcNow;

        var user = new User { DisplayName = "Default", CreatedAt = now };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var projectSyncId = Guid.NewGuid();
        db.Devices.Add(new Device { UserId = user.Id, DeviceId = Guid.NewGuid().ToString(), Label = "laptop", CreatedAt = now });
        db.Projects.Add(new Project { UserId = user.Id, SyncId = projectSyncId, Version = 1, Name = "Legacy work", CreatedAt = now });
        await db.SaveChangesAsync();

        Assert.Null(user.PasswordHash);
        return (user.Id, projectSyncId);
    }

    private static async Task<AuthCapabilitiesResponse> GetCapabilitiesAsync(WebApplicationFactory<Program> host)
    {
        var response = await host.CreateClient().GetAsync("/auth/capabilities");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthCapabilitiesResponse>())!;
    }

    /// <summary>
    /// Awaited inside the <c>using</c>, deliberately: returning the task would dispose the request — and
    /// with it the body stream the test host is still reading — before the send completed.
    /// </summary>
    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string email, string password, string? displayName, string? secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new RegisterUserRequest(email, password, displayName)),
        };
        if (secret is not null)
        {
            request.Headers.Add("X-Bootstrap-Secret", secret);
        }

        return await client.SendAsync(request);
    }
}
