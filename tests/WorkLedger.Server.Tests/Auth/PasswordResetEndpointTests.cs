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
/// S10's password reset: <c>POST /admin/users/{id}/password-reset</c> mints, and
/// <c>POST /auth/password/reset</c> spends.
/// <para>
/// On <see cref="UserAccountsFixture"/>'s private database, and starting from an empty one wherever the
/// first-registered-user-is-admin rule is the easiest way to get somebody who can issue a reset.
/// </para>
/// <para>
/// <b>The stamp cache is left at its default 30s.</b> "Every other device is signed out" is only a real
/// claim while the cache is on — with it disabled the assertion would pass without the eviction in
/// <c>MapResetPassword</c> existing at all. The test that cares warms the cache first.
/// </para>
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public class PasswordResetEndpointTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string AdminEmail = "operator@example.com";
    private const string UserEmail = "member@example.com";
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a whole new passphrase entirely";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- The happy path ---------------------------------------------------------------------

    /// <summary>
    /// The feature in one test: an admin who cannot see the password hands over a token, the user sets
    /// their own credential with it, and the old one stops working.
    /// </summary>
    [Fact]
    public async Task AResetToken_SetsANewPassword_AndRetiresTheOldOne()
    {
        using var host = Host();
        var (adminToken, memberId) = await SeedAdminAndMemberAsync(host);
        var issued = await IssueResetAsync(host, adminToken, memberId);

        var response = await RedeemAsync(host, issued.Token, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(host, UserEmail, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(host, UserEmail, Password)).StatusCode);
    }

    /// <summary>
    /// Why the response carries no token: the stamp bump kills every session, which is the correct
    /// outcome for a credential the person had lost control of. Asserted on the very next request, so
    /// the cache eviction is doing real work.
    /// </summary>
    [Fact]
    public async Task ARedeemedReset_SignsEveryDeviceOut()
    {
        using var host = Host();
        var (adminToken, memberId) = await SeedAdminAndMemberAsync(host);
        var laptop = await SignInAsync(host, UserEmail, Password, "member-laptop");
        var phone = await SignInAsync(host, UserEmail, Password, "member-phone");

        // Warm the cache, so the entry the endpoint evicts is demonstrably one that existed.
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, laptop).GetAsync("/auth/whoami")).StatusCode);

        var issued = await IssueResetAsync(host, adminToken, memberId);
        await AssertNoContent(RedeemAsync(host, issued.Token, NewPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Authorized(host, laptop).GetAsync("/auth/whoami")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Authorized(host, phone).GetAsync("/auth/whoami")).StatusCode);
    }

    /// <summary>
    /// The admin never learns or chooses the password: issuing a reset leaves the account exactly as it
    /// was until the user spends the token.
    /// </summary>
    [Fact]
    public async Task IssuingAReset_DoesNotChangeThePassword()
    {
        using var host = Host();
        var (adminToken, memberId) = await SeedAdminAndMemberAsync(host);
        var before = await PasswordHashAsync(memberId);

        await IssueResetAsync(host, adminToken, memberId);

        Assert.Equal(before, await PasswordHashAsync(memberId));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(host, UserEmail, Password)).StatusCode);
    }

    // ---- Refusals ---------------------------------------------------------------------------

    /// <summary>
    /// The three dead states answer <b>identically</b>, so an anonymous caller cannot learn which
    /// tokens have ever existed. Comparing the response bodies is the assertion — matching status codes
    /// alone would miss a message that named the cause.
    /// </summary>
    [Fact]
    public async Task AnUnknownExpiredOrSpentToken_AllAnswerTheSame400()
    {
        using var host = Host();
        var (adminToken, memberId) = await SeedAdminAndMemberAsync(host);

        var spent = await IssueResetAsync(host, adminToken, memberId);
        await AssertNoContent(RedeemAsync(host, spent.Token, NewPassword));
        var expiredToken = await SeedResetAsync(memberId, DateTime.UtcNow.AddMinutes(-1));

        var unknown = await RedeemAsync(host, "not-a-real-token", NewPassword);
        var expired = await RedeemAsync(host, expiredToken, NewPassword);
        var replayed = await RedeemAsync(host, spent.Token, NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);

        var body = await unknown.Content.ReadAsStringAsync();
        Assert.Equal(body, await expired.Content.ReadAsStringAsync());
        Assert.Equal(body, await replayed.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A token is single-use, not single-<i>attempt</i>: a rejected new password must not consume it, or
    /// a typo costs the user another round trip to their admin.
    /// </summary>
    [Fact]
    public async Task ANewPasswordBelowTheMinimum_Returns400_AndLeavesTheTokenSpendable()
    {
        using var host = Host();
        var (adminToken, memberId) = await SeedAdminAndMemberAsync(host);
        var issued = await IssueResetAsync(host, adminToken, memberId);

        var rejected = await RedeemAsync(host, issued.Token, "short");

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(host, UserEmail, Password)).StatusCode);
        await AssertNoContent(RedeemAsync(host, issued.Token, NewPassword));
    }

    /// <summary>
    /// Reissuing must not leave two live doors into one account — the one that went astray would be the
    /// one nobody is watching.
    /// </summary>
    [Fact]
    public async Task IssuingASecondReset_InvalidatesTheFirst()
    {
        using var host = Host();
        var (adminToken, memberId) = await SeedAdminAndMemberAsync(host);
        var first = await IssueResetAsync(host, adminToken, memberId);

        var second = await IssueResetAsync(host, adminToken, memberId);

        Assert.Equal(HttpStatusCode.BadRequest, (await RedeemAsync(host, first.Token, NewPassword)).StatusCode);
        await AssertNoContent(RedeemAsync(host, second.Token, NewPassword));

        using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.PasswordResets.CountAsync(r => r.UserId == memberId));
    }

    // ---- What a reset does and does not touch -----------------------------------------------

    /// <summary>
    /// Somebody resetting a password has very often just locked themselves out guessing at the old one.
    /// Leaving the counter armed would refuse the password they set seconds earlier.
    /// </summary>
    [Fact]
    public async Task ARedeemedReset_ClearsTheLockout()
    {
        using var host = Host(("Auth:MaxFailedLogins", "2"), ("Auth:LockoutMinutes", "60"));
        var (adminToken, memberId) = await SeedAdminAndMemberAsync(host);

        await LoginAsync(host, UserEmail, "wrong one");
        Assert.Equal(HttpStatusCode.Locked, (await LoginAsync(host, UserEmail, "wrong again")).StatusCode);

        var issued = await IssueResetAsync(host, adminToken, memberId);
        await AssertNoContent(RedeemAsync(host, issued.Token, NewPassword));

        // A 60-minute window has certainly not elapsed, so this can only be the reset clearing it.
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(host, UserEmail, NewPassword)).StatusCode);
    }

    /// <summary>
    /// Setting a credential and restoring access are different decisions, and only an admin makes the
    /// second. A suspended user who redeems a reset has a new password and still no way in.
    /// </summary>
    [Fact]
    public async Task ARedeemedReset_DoesNotUnsuspendTheAccount()
    {
        using var host = Host();
        var (adminToken, memberId) = await SeedAdminAndMemberAsync(host);
        await AssertNoContent(Authorized(host, adminToken).PostAsync($"/admin/users/{memberId}/disable", null));

        var issued = await IssueResetAsync(host, adminToken, memberId);
        await AssertNoContent(RedeemAsync(host, issued.Token, NewPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(host, UserEmail, NewPassword)).StatusCode);
        Assert.NotNull(await DisabledAtAsync(memberId));

        // And once re-enabled, the password the reset set is the one that works.
        await AssertNoContent(Authorized(host, adminToken).PostAsync($"/admin/users/{memberId}/enable", null));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(host, UserEmail, NewPassword)).StatusCode);
    }

    // ---- The admin route --------------------------------------------------------------------

    [Fact]
    public async Task IssuingAReset_IsAdminOnly_And404sForAnUnknownUser()
    {
        using var host = Host();
        var (adminToken, memberId) = await SeedAdminAndMemberAsync(host);
        var memberToken = await SignInAsync(host, UserEmail, Password, "member-laptop");

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await host.CreateClient().PostAsync($"/admin/users/{memberId}/password-reset", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Authorized(host, memberToken).PostAsync($"/admin/users/{memberId}/password-reset", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Authorized(host, adminToken).PostAsync("/admin/users/999999/password-reset", null)).StatusCode);
    }

    /// <summary>
    /// The pre-S6 credential-less row gets the same <c>409</c> naming <c>/auth/claim</c> that
    /// <c>POST /auth/password</c> gives it, rather than a token that could never produce a usable login.
    /// </summary>
    [Fact]
    public async Task IssuingAResetForACredentiallessRow_Returns409_NamingClaim()
    {
        var legacyId = await SeedUserAsync(email: null, passwordHash: null);
        await SeedUserAsync(AdminEmail, isAdmin: true);
        using var host = Host();
        var adminToken = await SignInAsync(host, AdminEmail, Password, "operator-laptop");

        var response = await Authorized(host, adminToken)
            .PostAsync($"/admin/users/{legacyId}/password-reset", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("/auth/claim", await response.Content.ReadAsStringAsync());
        using var db = fixture.CreateDbContext();
        Assert.Equal(0, await db.PasswordResets.CountAsync());
    }

    /// <summary>The token is in the mint response and nowhere else — the row holds only its hash.</summary>
    [Fact]
    public async Task TheStoredRow_HoldsAHashRatherThanTheToken()
    {
        using var host = Host();
        var (adminToken, memberId) = await SeedAdminAndMemberAsync(host);

        var issued = await IssueResetAsync(host, adminToken, memberId);

        using var db = fixture.CreateDbContext();
        var reset = await db.PasswordResets.SingleAsync();
        Assert.NotEqual(issued.Token, reset.TokenHash);
        Assert.Equal(SingleUseToken.Hash(issued.Token), reset.TokenHash);
        Assert.Equal(1, reset.CreatedByUserId);
        Assert.True(issued.ExpiresAtUtc > DateTime.UtcNow);
    }

    // ---- The two foreign keys ---------------------------------------------------------------

    /// <summary>
    /// The asymmetry from WL-165, asserted rather than assumed. The reset dies with the account it
    /// resets — there is nothing left to unlock — but outlives the admin who issued it, so the record
    /// of an intervention on a live account survives. Both together are why the account-deletion
    /// pipelines needed no change.
    /// </summary>
    [Fact]
    public async Task ThePasswordReset_DiesWithItsUser_ButOutlivesTheIssuingAdmin()
    {
        using var host = Host();
        var (adminToken, memberId) = await SeedAdminAndMemberAsync(host);
        await IssueResetAsync(host, adminToken, memberId);

        // A second admin, so the issuer can be deleted without tripping the last-admin guard.
        await SeedUserAsync("second.admin@example.com", isAdmin: true);
        var secondToken = await SignInAsync(host, "second.admin@example.com", Password, "second-laptop");
        await DeleteUserAsync(host, secondToken, await UserIdAsync(AdminEmail), AdminEmail);

        using (var db = fixture.CreateDbContext())
        {
            var reset = await db.PasswordResets.SingleAsync();
            Assert.Null(reset.CreatedByUserId);
            Assert.Equal(memberId, reset.UserId);
        }

        await DeleteUserAsync(host, secondToken, memberId, UserEmail);

        using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(0, await db.PasswordResets.CountAsync());
        }
    }

    // ---- helpers ----------------------------------------------------------------------------

    private WebApplicationFactory<Program> Host(params (string Key, string? Value)[] overrides) =>
        fixture.CreateHost([("Auth:LoginRateLimitPerMinute", "200"), .. overrides]);

    /// <summary>An admin (the first account, so it is one) plus an ordinary member, both signed in.</summary>
    private async Task<(string AdminToken, int MemberId)> SeedAdminAndMemberAsync(
        WebApplicationFactory<Program> host)
    {
        await SeedUserAsync(AdminEmail, isAdmin: true);
        var memberId = await SeedUserAsync(UserEmail);
        return (await SignInAsync(host, AdminEmail, Password, "operator-laptop"), memberId);
    }

    private static async Task<PasswordResetTokenResponse> IssueResetAsync(
        WebApplicationFactory<Program> host, string adminToken, int userId)
    {
        var response = await Authorized(host, adminToken)
            .PostAsync($"/admin/users/{userId}/password-reset", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PasswordResetTokenResponse>())!;
    }

    /// <summary>Anonymous by construction — no bearer token, which is the point of the endpoint.</summary>
    private static Task<HttpResponseMessage> RedeemAsync(
        WebApplicationFactory<Program> host, string token, string newPassword) =>
        host.CreateClient().PostAsJsonAsync(
            "/auth/password/reset", new ResetPasswordRequest(token, newPassword));

    /// <summary>A reset written straight to the database, for a state the API will not mint — an expiry in the past.</summary>
    private async Task<string> SeedResetAsync(int userId, DateTime expiresAtUtc)
    {
        var token = SingleUseToken.Generate();
        using var db = fixture.CreateDbContext();
        db.PasswordResets.Add(new PasswordReset
        {
            TokenHash = SingleUseToken.Hash(token),
            UserId = userId,
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            ExpiresAtUtc = expiresAtUtc,
        });
        await db.SaveChangesAsync();
        return token;
    }

    private async Task<int> SeedUserAsync(
        string? email = UserEmail, bool isAdmin = false, string? passwordHash = "")
    {
        using var db = fixture.CreateDbContext();
        var user = new User
        {
            Email = email,
            NormalizedEmail = email?.ToUpperInvariant(),
            PasswordHash = passwordHash == "" ? new PasswordService().Hash(Password) : passwordHash,
            IsAdmin = isAdmin,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static Task<HttpResponseMessage> LoginAsync(
        WebApplicationFactory<Program> host, string email, string password) =>
        host.CreateClient().PostAsJsonAsync(
            "/auth/login", new LoginRequest(email, password, "probe-device", null));

    private static async Task<string> SignInAsync(
        WebApplicationFactory<Program> host, string email, string password, string deviceId)
    {
        var response = await host.CreateClient().PostAsJsonAsync(
            "/auth/login", new LoginRequest(email, password, deviceId, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task DeleteUserAsync(
        WebApplicationFactory<Program> host, string adminToken, int userId, string confirmEmail)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/users/{userId}")
        {
            Content = JsonContent.Create(new AdminDeleteUserRequest(confirmEmail)),
        };
        var response = await Authorized(host, adminToken).SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task AssertNoContent(Task<HttpResponseMessage> call) =>
        Assert.Equal(HttpStatusCode.NoContent, (await call).StatusCode);

    private async Task<string?> PasswordHashAsync(int userId)
    {
        using var db = fixture.CreateDbContext();
        return (await db.Users.SingleAsync(u => u.Id == userId)).PasswordHash;
    }

    private async Task<DateTime?> DisabledAtAsync(int userId)
    {
        using var db = fixture.CreateDbContext();
        return (await db.Users.SingleAsync(u => u.Id == userId)).DisabledAtUtc;
    }

    private async Task<int> UserIdAsync(string email)
    {
        using var db = fixture.CreateDbContext();
        return (await db.Users.SingleAsync(u => u.NormalizedEmail == email.ToUpperInvariant())).Id;
    }
}
