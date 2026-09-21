using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Tests.Auth;

/// <summary>
/// S10's invites: the three <c>/admin/invites</c> routes, and what <c>POST /auth/register</c> now accepts.
/// <para>
/// On <see cref="UserAccountsFixture"/>'s private database, for the reason its own doc gives and one
/// more: most assertions here begin from an <i>empty</i> database, because the first registered user
/// being an admin is what gives these tests somebody who can issue an invite in the first place.
/// </para>
/// <para>
/// <b>What is deliberately not asserted: the raw token's shape.</b> Its length and alphabet are
/// <c>SingleUseToken</c>'s business and may change; what callers depend on is that it round-trips
/// through a redemption exactly once, which is what these tests pin instead.
/// </para>
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public class InviteEndpointTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string AdminEmail = "operator@example.com";
    private const string InviteeEmail = "newcomer@example.com";
    private const string OtherEmail = "someone.else@example.com";
    private const string Password = "correct horse battery staple";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- The happy path ---------------------------------------------------------------------

    /// <summary>
    /// The whole point of the feature in one test: an admin issues an invite, somebody else registers
    /// with it and no shared secret, and the row records who invited whom.
    /// </summary>
    [Fact]
    public async Task AnInvite_RegistersAUser_WithoutTheSharedSecret_AndIsStampedRedeemed()
    {
        using var host = Host();
        var adminToken = await RegisterFirstAdminAsync(host);
        var created = await CreateInviteAsync(host, adminToken);

        var response = await RegisterWithInviteAsync(host, InviteeEmail, created.Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = (await response.Content.ReadFromJsonAsync<UserSummary>())!;
        Assert.Equal(InviteeEmail, user.Email);

        using var db = fixture.CreateDbContext();
        var invite = await db.Invites.SingleAsync(i => i.Id == created.Invite.Id);
        Assert.NotNull(invite.RedeemedAtUtc);
        Assert.Equal(user.Id, invite.RedeemedByUserId);
        Assert.Equal(1, invite.CreatedByUserId);

        // And the invited user is emphatically not an admin — only the first account on an empty
        // database gets that, and an invite says nothing about privilege.
        Assert.False((await db.Users.SingleAsync(u => u.Id == user.Id)).IsAdmin);
    }

    [Fact]
    public async Task ARedeemedInvite_CannotBeUsedAgain()
    {
        using var host = Host();
        var adminToken = await RegisterFirstAdminAsync(host);
        var created = await CreateInviteAsync(host, adminToken);
        (await RegisterWithInviteAsync(host, InviteeEmail, created.Token)).EnsureSuccessStatusCode();

        var second = await RegisterWithInviteAsync(host, OtherEmail, created.Token);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("already been used", await second.Content.ReadAsStringAsync());
        await AssertUserCountAsync(2);
    }

    // ---- Refusals ---------------------------------------------------------------------------

    /// <summary>
    /// An unknown token and no token at all get the same <c>401</c>: both are indistinguishable from a
    /// guess at an endpoint anyone can reach. The <c>409</c>s elsewhere in this file are the contrast —
    /// they mean "this is a real invite, and here is what is wrong with it".
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    public async Task RegisteringWithNoUsableCredential_Returns401(string? token)
    {
        using var host = Host();
        await RegisterFirstAdminAsync(host);

        var response = await RegisterWithInviteAsync(host, InviteeEmail, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertUserCountAsync(1);
    }

    [Fact]
    public async Task AnExpiredInvite_Returns409_AndIsNotRedeemed()
    {
        using var host = Host();
        await RegisterFirstAdminAsync(host);
        var (token, inviteId) = await SeedInviteAsync(expiresAtUtc: DateTime.UtcNow.AddMinutes(-1));

        var response = await RegisterWithInviteAsync(host, InviteeEmail, token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("expired", await response.Content.ReadAsStringAsync());
        await AssertNotRedeemedAsync(inviteId);
        await AssertUserCountAsync(1);
    }

    /// <summary>
    /// A bound invite redeemed from the wrong address is refused <b>and survives</b>, so the person it
    /// was actually issued to can still use it. Burning it here would turn a forwarded token into a
    /// denial of service against the intended recipient.
    /// </summary>
    [Fact]
    public async Task AnInviteBoundToAnotherAddress_Returns409_AndStaysRedeemable()
    {
        using var host = Host();
        var adminToken = await RegisterFirstAdminAsync(host);
        var created = await CreateInviteAsync(host, adminToken, email: InviteeEmail);

        var wrong = await RegisterWithInviteAsync(host, OtherEmail, created.Token);

        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Contains("different email address", await wrong.Content.ReadAsStringAsync());
        await AssertNotRedeemedAsync(created.Invite.Id);

        // The address it was issued for still works, and case does not matter.
        var right = await RegisterWithInviteAsync(host, InviteeEmail.ToUpperInvariant(), created.Token);
        Assert.Equal(HttpStatusCode.Created, right.StatusCode);
    }

    /// <summary>
    /// The ordering rule from the plan: an invite consumed by a registration that then fails must not be
    /// burned. Redemption and the user insert share one transaction precisely so this holds.
    /// </summary>
    [Fact]
    public async Task ARegistrationThatFailsOnADuplicateEmail_LeavesTheInviteUnredeemed()
    {
        using var host = Host();
        var adminToken = await RegisterFirstAdminAsync(host);
        var created = await CreateInviteAsync(host, adminToken);

        // The admin's own address is already taken.
        var response = await RegisterWithInviteAsync(host, AdminEmail, created.Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertNotRedeemedAsync(created.Invite.Id);

        // Proof it was not merely left unstamped but is genuinely still spendable.
        Assert.Equal(
            HttpStatusCode.Created,
            (await RegisterWithInviteAsync(host, InviteeEmail, created.Token)).StatusCode);
    }

    [Fact]
    public async Task AnInvalidPassword_Returns400_AndLeavesTheInviteUnredeemed()
    {
        using var host = Host();
        var adminToken = await RegisterFirstAdminAsync(host);
        var created = await CreateInviteAsync(host, adminToken);

        var response = await host.CreateClient().PostAsJsonAsync(
            "/auth/register", new RegisterUserRequest(InviteeEmail, "short", null, created.Token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNotRedeemedAsync(created.Invite.Id);
    }

    // ---- Auth:SignupSecretEnabled -----------------------------------------------------------

    /// <summary>
    /// The default is unchanged behaviour: the shared secret still registers, exactly as before invites
    /// existed. This is the test that an upgrading deployment does not break.
    /// </summary>
    [Fact]
    public async Task WithTheDefaultConfiguration_TheSharedSecretStillRegisters()
    {
        using var host = Host();

        var response = await RegisterWithSecretAsync(host, AdminEmail);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// And with the secret switched off, an invite is the only way in — which is the state the feature
    /// exists to make reachable.
    /// </summary>
    [Fact]
    public async Task WithTheSharedSecretDisabled_OnlyAnInviteRegisters()
    {
        // Seeded directly: with the secret off there is no way to create the first admin through the API.
        var adminId = await SeedUserAsync(AdminEmail, isAdmin: true);
        using var host = Host(("Auth:SignupSecretEnabled", "false"));
        var adminToken = await SignInAsync(host, AdminEmail, "operator-laptop");

        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterWithSecretAsync(host, InviteeEmail)).StatusCode);

        var created = await CreateInviteAsync(host, adminToken);
        Assert.Equal(
            HttpStatusCode.Created,
            (await RegisterWithInviteAsync(host, InviteeEmail, created.Token)).StatusCode);

        using var db = fixture.CreateDbContext();
        Assert.Equal(adminId, (await db.Invites.SingleAsync()).CreatedByUserId);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task Capabilities_ReportsWhetherTheSecretIsAccepted_AndAlwaysThatInvitesAre(
        string configured, bool expected)
    {
        using var host = Host(("Auth:SignupSecretEnabled", configured));

        var capabilities = (await host.CreateClient()
            .GetFromJsonAsync<AuthCapabilitiesResponse>("/auth/capabilities"))!;

        Assert.Equal(expected, capabilities.RegistrationRequiresSecret);
        Assert.True(capabilities.RegistrationAcceptsInvite);
    }

    // ---- The admin routes -------------------------------------------------------------------

    /// <summary>
    /// The token is in the create response and nowhere else. This is the test that a later "just add the
    /// token to the list response for convenience" change has to break.
    /// </summary>
    [Fact]
    public async Task ListingInvites_ReturnsMetadataButNeverTheToken()
    {
        using var host = Host();
        var adminToken = await RegisterFirstAdminAsync(host);
        var created = await CreateInviteAsync(host, adminToken, email: InviteeEmail, note: "contractor, Q3");

        var listResponse = await Authorized(host, adminToken).GetAsync("/admin/invites");
        var body = await listResponse.Content.ReadAsStringAsync();
        var invites = (await Authorized(host, adminToken)
            .GetFromJsonAsync<List<InviteSummary>>("/admin/invites"))!;

        Assert.DoesNotContain(created.Token, body);
        var only = Assert.Single(invites);
        Assert.Equal(InviteeEmail.ToUpperInvariant(), only.Email);
        Assert.Equal("contractor, Q3", only.Note);
        Assert.Equal(1, only.CreatedByUserId);
        Assert.Null(only.RedeemedAtUtc);

        // And the stored value is a hash, not the token.
        using var db = fixture.CreateDbContext();
        Assert.NotEqual(created.Token, (await db.Invites.SingleAsync()).TokenHash);
    }

    [Fact]
    public async Task RevokingAnUnredeemedInvite_Returns204_AndItStopsWorking()
    {
        using var host = Host();
        var adminToken = await RegisterFirstAdminAsync(host);
        var created = await CreateInviteAsync(host, adminToken);

        var revoke = await Authorized(host, adminToken).DeleteAsync($"/admin/invites/{created.Invite.Id}");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await RegisterWithInviteAsync(host, InviteeEmail, created.Token)).StatusCode);
    }

    /// <summary>
    /// A redeemed invite is kept on purpose — it is the record of how a live account came to exist — so
    /// revoking one is a <c>409</c> rather than a silent erasure of that record.
    /// </summary>
    [Fact]
    public async Task RevokingARedeemedInvite_Returns409_AndKeepsTheRow()
    {
        using var host = Host();
        var adminToken = await RegisterFirstAdminAsync(host);
        var created = await CreateInviteAsync(host, adminToken);
        (await RegisterWithInviteAsync(host, InviteeEmail, created.Token)).EnsureSuccessStatusCode();

        var revoke = await Authorized(host, adminToken).DeleteAsync($"/admin/invites/{created.Invite.Id}");

        Assert.Equal(HttpStatusCode.Conflict, revoke.StatusCode);
        using var db = fixture.CreateDbContext();
        Assert.True(await db.Invites.AnyAsync(i => i.Id == created.Invite.Id));
    }

    [Fact]
    public async Task InviteRoutes_AreForbiddenForANonAdmin_AndUnauthorizedWithoutAToken()
    {
        using var host = Host();
        var adminToken = await RegisterFirstAdminAsync(host);
        var created = await CreateInviteAsync(host, adminToken);
        (await RegisterWithInviteAsync(host, InviteeEmail, created.Token)).EnsureSuccessStatusCode();
        var memberToken = await SignInAsync(host, InviteeEmail, "member-laptop");

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await host.CreateClient().GetAsync("/admin/invites")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Authorized(host, memberToken).GetAsync("/admin/invites")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Authorized(host, memberToken).PostAsJsonAsync("/admin/invites", new InviteRequest())).StatusCode);

        // Revocation too. A member who could delete invites could not read the list to find an id,
        // but "they would have to guess" is not an access control — the policy is.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await host.CreateClient().DeleteAsync($"/admin/invites/{created.Invite.Id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Authorized(host, memberToken).DeleteAsync($"/admin/invites/{created.Invite.Id}")).StatusCode);
    }

    [Fact]
    public async Task CreatingAnInvite_RejectsAMalformedEmail_AndANonPositiveExpiry()
    {
        using var host = Host();
        var adminToken = await RegisterFirstAdminAsync(host);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Authorized(host, adminToken)
                .PostAsJsonAsync("/admin/invites", new InviteRequest(Email: "not-an-email"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Authorized(host, adminToken)
                .PostAsJsonAsync("/admin/invites", new InviteRequest(ExpiresInDays: 0))).StatusCode);

        using var db = fixture.CreateDbContext();
        Assert.Equal(0, await db.Invites.CountAsync());
    }

    [Fact]
    public async Task RevokingAnInviteThatDoesNotExist_Returns404()
    {
        using var host = Host();
        var adminToken = await RegisterFirstAdminAsync(host);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Authorized(host, adminToken).DeleteAsync("/admin/invites/999999")).StatusCode);
    }

    // ---- Outliving the accounts at either end -----------------------------------------------

    /// <summary>
    /// The <c>ON DELETE SET NULL</c> decision, asserted rather than assumed: deleting the admin who
    /// issued an invite must not take the record of it — or the account it created — down too. This is
    /// also what lets both account-deletion pipelines stay exactly as S8 and S10B left them.
    /// </summary>
    [Fact]
    public async Task DeletingTheIssuingAdmin_KeepsTheInvite_WithANullCreator()
    {
        using var host = Host();
        var adminToken = await RegisterFirstAdminAsync(host);
        var created = await CreateInviteAsync(host, adminToken);
        (await RegisterWithInviteAsync(host, InviteeEmail, created.Token)).EnsureSuccessStatusCode();

        // A second admin, so the first can be deleted without tripping the last-admin guard.
        var secondAdminId = await SeedUserAsync(OtherEmail, isAdmin: true);
        var secondToken = await SignInAsync(host, OtherEmail, "second-laptop");
        var firstAdminId = await UserIdAsync(AdminEmail);

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/admin/users/{firstAdminId}")
        {
            Content = JsonContent.Create(new AdminDeleteUserRequest(AdminEmail)),
        };
        var response = await Authorized(host, secondToken).SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var db = fixture.CreateDbContext();
        var invite = await db.Invites.SingleAsync(i => i.Id == created.Invite.Id);
        Assert.Null(invite.CreatedByUserId);
        Assert.NotNull(invite.RedeemedByUserId);
        Assert.NotEqual(0, secondAdminId);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private WebApplicationFactory<Program> Host(params (string Key, string? Value)[] overrides) =>
        fixture.CreateHost([("Auth:LoginRateLimitPerMinute", "200"), .. overrides]);

    /// <summary>
    /// Registers the first account on an empty database — which makes it the admin — and signs it in.
    /// </summary>
    private static async Task<string> RegisterFirstAdminAsync(WebApplicationFactory<Program> host)
    {
        (await RegisterWithSecretAsync(host, AdminEmail)).EnsureSuccessStatusCode();
        return await SignInAsync(host, AdminEmail, "operator-laptop");
    }

    private static Task<HttpResponseMessage> RegisterWithSecretAsync(
        WebApplicationFactory<Program> host, string email)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Bootstrap-Secret", TestConfig.BootstrapSecret);
        return client.PostAsJsonAsync("/auth/register", new RegisterUserRequest(email, Password, null));
    }

    /// <summary>No bootstrap secret header at all — the invite has to carry the whole gate.</summary>
    private static Task<HttpResponseMessage> RegisterWithInviteAsync(
        WebApplicationFactory<Program> host, string email, string? token) =>
        host.CreateClient().PostAsJsonAsync(
            "/auth/register", new RegisterUserRequest(email, Password, null, token));

    private static async Task<InviteResponse> CreateInviteAsync(
        WebApplicationFactory<Program> host,
        string adminToken,
        string? email = null,
        string? note = null)
    {
        var response = await Authorized(host, adminToken)
            .PostAsJsonAsync("/admin/invites", new InviteRequest(email, null, note));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InviteResponse>())!;
    }

    /// <summary>
    /// An invite written straight to the database, for the states the API will not produce — an expiry
    /// in the past being the one that matters.
    /// </summary>
    private async Task<(string Token, int InviteId)> SeedInviteAsync(DateTime expiresAtUtc)
    {
        var token = SingleUseToken.Generate();
        using var db = fixture.CreateDbContext();
        var invite = new Invite
        {
            TokenHash = SingleUseToken.Hash(token),
            CreatedByUserId = null,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            ExpiresAtUtc = expiresAtUtc,
        };
        db.Invites.Add(invite);
        await db.SaveChangesAsync();
        return (token, invite.Id);
    }

    private async Task<int> SeedUserAsync(string email, bool isAdmin = false)
    {
        using var db = fixture.CreateDbContext();
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = new PasswordService().Hash(Password),
            IsAdmin = isAdmin,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<string> SignInAsync(
        WebApplicationFactory<Program> host, string email, string deviceId)
    {
        var response = await host.CreateClient().PostAsJsonAsync(
            "/auth/login", new LoginRequest(email, Password, deviceId, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<int> UserIdAsync(string email)
    {
        using var db = fixture.CreateDbContext();
        return (await db.Users.SingleAsync(u => u.NormalizedEmail == email.ToUpperInvariant())).Id;
    }

    private async Task AssertNotRedeemedAsync(int inviteId)
    {
        using var db = fixture.CreateDbContext();
        var invite = await db.Invites.SingleAsync(i => i.Id == inviteId);
        Assert.Null(invite.RedeemedAtUtc);
        Assert.Null(invite.RedeemedByUserId);
    }

    private async Task AssertUserCountAsync(int expected)
    {
        using var db = fixture.CreateDbContext();
        Assert.Equal(expected, await db.Users.CountAsync());
    }
}
