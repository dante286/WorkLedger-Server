using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WorkLedger.Contracts.Auth;
using WorkLedger.Contracts.Sync;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Tests.Auth;

/// <summary>
/// <c>POST /auth/login</c>, <c>POST /auth/password</c>, and the two mechanisms they exist to carry:
/// security-stamp revocation and brute-force defense.
/// <para>
/// These need <see cref="UserAccountsFixture"/>'s private database rather than the shared one — the
/// lockout assertions are about a specific user's counters and must not race another suite signing in,
/// and each test needs its own host so it gets a fresh rate limiter and a fresh stamp cache.
/// </para>
/// <para>
/// <b>Users are seeded straight into the database, not registered over HTTP.</b> Registration shares the
/// rate-limit policy with login, so a setup call would silently spend one of the permits a rate-limit
/// test is counting. <see cref="Login_AfterRegisteringOverHttp_Works"/> covers the real journey once.
/// </para>
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public class LoginEndpointTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string Email = "sam@example.com";
    private const string Password = "correct horse battery staple";
    private const string WrongPassword = "correct horse battery stapler";

    /// <summary>
    /// Small enough that a lockout is two requests away, so the suite is not spending PBKDF2 cycles on
    /// five failures per test.
    /// </summary>
    private const string MaxFailedLogins = "2";

    /// <summary>
    /// Three seconds. Rules 2 and 3 can only be proven by a test that lives through a whole window and
    /// then submits another attempt, and faking the expiry by writing <c>LockoutEndUtc</c> directly
    /// would exercise the reset path without proving the window is honored at all. See
    /// <c>AuthOptions.LockoutWindow</c> for why the option binds fractionally.
    /// </summary>
    private const string LockoutMinutes = "0.05";

    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(0.05);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Login_WithCorrectCredentials_Returns200AndATokenThatWorks()
    {
        await SeedUserAsync(displayName: "Sam");
        using var host = fixture.CreateHost();

        var response = await LoginAsync(host.CreateClient(), Email, Password, "device-a", "Laptop");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));
        Assert.True(body.ExpiresAtUtc > DateTime.UtcNow);
        Assert.Equal(Email, body.User.Email);
        Assert.Equal("Sam", body.User.DisplayName);

        var pull = await Authorized(host, body.Token).GetAsync("/sync/pull");
        Assert.Equal(HttpStatusCode.OK, pull.StatusCode);
    }

    [Fact]
    public async Task Login_AfterRegisteringOverHttp_Works()
    {
        using var host = fixture.CreateHost();
        var client = host.CreateClient();
        using var register = new HttpRequestMessage(HttpMethod.Post, "/auth/register")
        {
            Content = JsonContent.Create(new RegisterUserRequest(Email, Password, "Sam")),
        };
        register.Headers.Add("X-Bootstrap-Secret", fixture.BootstrapSecret);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(register)).StatusCode);

        var response = await LoginAsync(client, Email, Password, "device-a", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_IsCaseInsensitiveOnTheEmailAndTrimsIt()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost();

        var response = await LoginAsync(host.CreateClient(), "  SAM@EXAMPLE.COM ", Password, "device-a", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The uniform 401. Three very different causes — no such account, a wrong password, and the
    /// credential-less unclaimed row — must be indistinguishable from outside, or an anonymous caller
    /// can enumerate accounts. Asserted on the body as well as the status: a differing message would
    /// leak exactly as much as a differing code.
    /// </summary>
    [Fact]
    public async Task Login_UnknownEmail_WrongPassword_AndCredentialLessUser_AreIndistinguishable()
    {
        await SeedUserAsync();
        await SeedUnclaimedUserAsync();
        using var host = fixture.CreateHost();
        var client = host.CreateClient();

        var wrongPassword = await LoginAsync(client, Email, WrongPassword, "device-a", null);
        var unknownEmail = await LoginAsync(client, "nobody@example.com", Password, "device-a", null);
        var credentialLess = await LoginAsync(client, "legacy@example.com", Password, "device-a", null);

        foreach (var response in new[] { wrongPassword, unknownEmail, credentialLess })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var bodies = new HashSet<string>
        {
            await wrongPassword.Content.ReadAsStringAsync(),
            await unknownEmail.Content.ReadAsStringAsync(),
            await credentialLess.Content.ReadAsStringAsync(),
        };
        Assert.Single(bodies);
        Assert.Contains("Invalid email or password.", bodies.Single());
    }

    /// <summary>
    /// A closed server still lets its existing users in. Easy to get wrong by hanging login off the same
    /// flag registration uses, which would lock every device out of an instance the moment its operator
    /// switched signups off.
    /// </summary>
    [Fact]
    public async Task Login_IsUnaffectedByRegistrationBeingDisabled()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost(("Auth:RegistrationEnabled", "false"));

        var response = await LoginAsync(host.CreateClient(), Email, Password, "device-a", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithoutADeviceId_Returns400()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost();

        var response = await LoginAsync(host.CreateClient(), Email, Password, "   ", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A hash in the older Identity V2 format still logs in, and is replaced on the way through. The
    /// whole reason for using a versioned hasher is that an iteration-count bump years from now is a
    /// detectable format change rather than a silent lockout.
    /// </summary>
    [Fact]
    public async Task Login_WithAnOlderHashFormat_SucceedsAndRehashes()
    {
        var legacyHasher = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2,
        }));
        var legacyHash = legacyHasher.HashPassword(null!, Password);
        await SeedUserAsync(passwordHash: legacyHash);
        using var host = fixture.CreateHost();

        var response = await LoginAsync(host.CreateClient(), Email, Password, "device-a", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var db = fixture.CreateDbContext();
        var stored = (await db.Users.SingleAsync()).PasswordHash;
        Assert.NotEqual(legacyHash, stored);
        Assert.Equal(PasswordVerification.Success, new PasswordService().Verify(stored!, Password));
    }

    [Fact]
    public async Task Login_StampsLastLoginAndClearsCounters()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost(("Auth:MaxFailedLogins", MaxFailedLogins));
        var client = host.CreateClient();
        await LoginAsync(client, Email, WrongPassword, "device-a", null);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, Email, Password, "device-a", null)).StatusCode);

        var user = await ReloadUserAsync();
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockoutEndUtc);
        Assert.NotNull(user.LastLoginUtc);
    }

    [Fact]
    public async Task Login_FirstTime_CreatesTheDeviceRowWithItsLabel()
    {
        var userId = await SeedUserAsync();
        using var host = fixture.CreateHost();

        await LoginAsync(host.CreateClient(), Email, Password, "device-a", "Laptop");

        using var db = fixture.CreateDbContext();
        var device = await db.Devices.SingleAsync();
        Assert.Equal(userId, device.UserId);
        Assert.Equal("device-a", device.DeviceId);
        Assert.Equal("Laptop", device.Label);
        Assert.NotNull(device.LastSeenUtc);
    }

    /// <summary>
    /// Enrollment is idempotent, exactly as <c>/auth/register-device</c> was: the same install signing in
    /// again reuses its row and refreshes <c>LastSeenUtc</c>, and a blank label leaves the one it had.
    /// </summary>
    [Fact]
    public async Task Login_Again_WithTheSameDeviceId_ReusesTheRow()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost();
        var client = host.CreateClient();
        await LoginAsync(client, Email, Password, "device-a", "Laptop");
        var firstSeen = (await SingleDeviceAsync()).LastSeenUtc;

        await LoginAsync(client, Email, Password, "device-a", null);

        var device = await SingleDeviceAsync();
        Assert.Equal("Laptop", device.Label);
        Assert.True(device.LastSeenUtc >= firstSeen);
    }

    [Fact]
    public async Task Login_WithANewDeviceId_AddsASecondRow()
    {
        var userId = await SeedUserAsync();
        using var host = fixture.CreateHost();
        var client = host.CreateClient();

        await LoginAsync(client, Email, Password, "device-a", "Laptop");
        await LoginAsync(client, Email, Password, "device-b", "Desktop");

        using var db = fixture.CreateDbContext();
        Assert.Equal(2, await db.Devices.CountAsync(d => d.UserId == userId));
    }

    /// <summary>
    /// The unique index is on <c>(UserId, DeviceId)</c>, not on <c>DeviceId</c> alone, so one install's
    /// id under two accounts is two unrelated devices. Anything else would hand a second user a token
    /// pointing at the first user's device row — and with it, the first user's data.
    /// </summary>
    [Fact]
    public async Task TheSameDeviceId_UnderTwoUsers_StaysTwoIsolatedDevices()
    {
        var samId = await SeedUserAsync();
        await SeedUserAsync("alex@example.com");
        var samsProject = await SeedProjectAsync(samId, "Sam's work");
        using var host = fixture.CreateHost();
        var client = host.CreateClient();

        var sam = await LoggedInTokenAsync(client, Email, "shared-device-id");
        var alex = await LoggedInTokenAsync(client, "alex@example.com", "shared-device-id");

        Assert.NotEqual(sam, alex);
        using var db = fixture.CreateDbContext();
        Assert.Equal(2, await db.Devices.CountAsync(d => d.DeviceId == "shared-device-id"));

        var samsPull = await PullAsync(host, sam);
        var alexsPull = await PullAsync(host, alex);
        Assert.Contains(samsPull.Changes, c => c.EntityId == samsProject);
        Assert.DoesNotContain(alexsPull.Changes, c => c.EntityId == samsProject);
    }

    [Fact]
    public async Task WhoAmI_CarriesTheEmailAndDisplayName()
    {
        var userId = await SeedUserAsync(displayName: "Sam");
        using var host = fixture.CreateHost();
        var token = await LoggedInTokenAsync(host.CreateClient(), Email, "device-a");

        var body = await Authorized(host, token).GetFromJsonAsync<WhoAmIResponse>("/auth/whoami");

        Assert.Equal(userId, body!.UserId);
        Assert.Equal(Email, body.Email);
        Assert.Equal("Sam", body.DisplayName);
        Assert.Equal("device-a", body.DeviceId);
    }

    /// <summary>
    /// The revocation the security stamp exists for. The bump is applied straight to the database, which
    /// is what S8's endpoints and an operator's <c>psql</c> session both amount to, with the cache off so
    /// the assertion is about the check rather than about the TTL.
    /// </summary>
    [Fact]
    public async Task Token_StopsWorking_OnceTheSecurityStampIsBumped()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost(("Auth:StampCacheSeconds", "0"));
        var token = await LoggedInTokenAsync(host.CreateClient(), Email, "device-a");
        var client = Authorized(host, token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/whoami")).StatusCode);

        using (var db = fixture.CreateDbContext())
        {
            var user = await db.Users.SingleAsync();
            user.SecurityStamp = Guid.NewGuid().ToString();
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/whoami")).StatusCode);
    }

    [Fact]
    public async Task Token_StopsWorking_OnceItsDeviceRowIsDeleted()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost(("Auth:StampCacheSeconds", "0"));
        var token = await LoggedInTokenAsync(host.CreateClient(), Email, "device-a");
        var client = Authorized(host, token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/whoami")).StatusCode);

        using (var db = fixture.CreateDbContext())
        {
            db.Devices.RemoveRange(await db.Devices.ToListAsync());
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/whoami")).StatusCode);
    }

    [Fact]
    public async Task Token_StopsWorking_OnceItsUserIsDeleted()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost(("Auth:StampCacheSeconds", "0"));
        var token = await LoggedInTokenAsync(host.CreateClient(), Email, "device-a");
        var client = Authorized(host, token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/whoami")).StatusCode);

        await fixture.ResetAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/whoami")).StatusCode);
    }

    /// <summary>
    /// The other half of the same tradeoff, asserted so nobody "fixes" it: with the cache on, a stamp
    /// bumped from outside this process is not noticed until the TTL lapses. That lag is what buys one
    /// database read per authenticated request instead of one per request, and the endpoints that revoke
    /// evict the key directly so the common case never sees it.
    /// </summary>
    [Fact]
    public async Task Token_KeepsWorkingBriefly_WhenTheStampChangesBehindTheCache()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost(("Auth:StampCacheSeconds", "300"));
        var token = await LoggedInTokenAsync(host.CreateClient(), Email, "device-a");
        var client = Authorized(host, token);
        // Populates the cache entry; without this first call there would be nothing stale to observe.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/whoami")).StatusCode);

        using (var db = fixture.CreateDbContext())
        {
            var user = await db.Users.SingleAsync();
            user.SecurityStamp = Guid.NewGuid().ToString();
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/whoami")).StatusCode);
    }

    [Fact]
    public async Task Lockout_AfterMaxFailedLogins_Returns423WithRetryAfter_AndRefusesTheCorrectPassword()
    {
        await SeedUserAsync();
        using var host = LockoutHost();
        var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, Email, WrongPassword, "device-a", null)).StatusCode);
        var locked = await LoginAsync(client, Email, WrongPassword, "device-a", null);

        Assert.Equal(HttpStatusCode.Locked, locked.StatusCode);
        Assert.NotNull(locked.Headers.RetryAfter);
        Assert.True(locked.Headers.RetryAfter!.Delta > TimeSpan.Zero);

        // The point of a lockout: the right password is refused too, or it would only slow down a
        // guesser by the length of one request.
        var correct = await LoginAsync(client, Email, Password, "device-a", null);
        Assert.Equal(HttpStatusCode.Locked, correct.StatusCode);
    }

    /// <summary>Rule 1: a success short of the threshold puts the counter back to zero.</summary>
    [Fact]
    public async Task Lockout_ASuccessBeforeTheThreshold_ResetsTheCounter()
    {
        await SeedUserAsync();
        using var host = LockoutHost();
        var client = host.CreateClient();

        await LoginAsync(client, Email, WrongPassword, "device-a", null);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, Email, Password, "device-a", null)).StatusCode);
        Assert.Equal(0, (await ReloadUserAsync()).FailedLoginCount);

        // Proof the counter really reset rather than merely being reported as zero: one more failure
        // would have locked the account if the earlier one still counted.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, Email, WrongPassword, "device-a", null)).StatusCode);
    }

    /// <summary>
    /// <b>Rule 2, and the only test that catches the defect.</b> Without the reset-on-expiry the counter
    /// stays at the threshold forever, so the first wrong password after the window lifts re-locks the
    /// account instantly and the account is permanently on one-typo-per-window. The window is waited out
    /// for real rather than faked by writing <c>LockoutEndUtc</c>, because a fake would exercise the
    /// reset while proving nothing about the window being honored.
    /// </summary>
    [Fact]
    public async Task Lockout_Expiring_ResetsTheCounter_SoTheNextWrongPasswordIs401NotAnother423()
    {
        await SeedUserAsync();
        using var host = LockoutHost();
        var client = host.CreateClient();
        await LockAsync(client);

        await Task.Delay(LockoutWindow + TimeSpan.FromSeconds(1));

        var afterExpiry = await LoginAsync(client, Email, WrongPassword, "device-a", null);
        Assert.Equal(HttpStatusCode.Unauthorized, afterExpiry.StatusCode);
        Assert.Equal(1, (await ReloadUserAsync()).FailedLoginCount);

        // And the correct password gets in, which is what a locked-out owner actually needs.
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, Email, Password, "device-a", null)).StatusCode);
    }

    /// <summary>
    /// <b>Rule 3.</b> A 423 is a pure read: it neither increments the counter nor re-stamps the window.
    /// The naive "extend on every refusal" lets anyone who knows the email hold the account locked
    /// indefinitely by polling — and on a self-hosted box the locked-out user is the operator, whose only
    /// recovery would be a SQL <c>UPDATE</c> against the volume.
    /// </summary>
    [Fact]
    public async Task Lockout_RepeatedRefusals_NeitherIncrementTheCounterNorExtendTheWindow()
    {
        await SeedUserAsync();
        using var host = LockoutHost();
        var client = host.CreateClient();
        await LockAsync(client);

        var locked = await ReloadUserAsync();
        var lockoutEnd = locked.LockoutEndUtc;
        var failures = locked.FailedLoginCount;
        Assert.NotNull(lockoutEnd);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.Locked, (await LoginAsync(client, Email, Password, "device-a", null)).StatusCode);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        var polled = await ReloadUserAsync();
        Assert.Equal(lockoutEnd, polled.LockoutEndUtc);
        Assert.Equal(failures, polled.FailedLoginCount);

        // The window has to end when it was first computed to end — not one window after the last poll.
        var remaining = lockoutEnd!.Value - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining + TimeSpan.FromSeconds(1));
        }

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, Email, Password, "device-a", null)).StatusCode);
    }

    /// <summary>
    /// <c>Auth:MaxFailedLogins=0</c> disables the lockout from the next request, including for an
    /// account still inside a window stamped while it was armed: neither password may answer 423.
    /// </summary>
    [Fact]
    public async Task Lockout_Disabled_IgnoresAWindowStampedWhileItWasArmed()
    {
        await SeedUserAsync();
        using (var armed = LockoutHost())
        {
            await LockAsync(armed.CreateClient());
        }

        Assert.NotNull((await ReloadUserAsync()).LockoutEndUtc);

        using var host = fixture.CreateHost(("Auth:MaxFailedLogins", "0"));
        var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, Email, WrongPassword, "device-a", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, Email, Password, "device-a", null)).StatusCode);
    }

    /// <summary>
    /// Per-IP throttling, which is the defense the per-user lockout cannot provide: a caller spraying one
    /// password across many email addresses never trips any single account's counter.
    /// <para>
    /// The window is left at its default and only the permit limit is lowered, because a fresh host means
    /// a fresh limiter — nothing has to wait for this bucket to refill.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RateLimit_ExceedingTheLimit_Returns429()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost(
            ("Auth:LoginRateLimitPerMinute", "3"),
            // Well above the request count, so the lockout cannot be what refuses the last attempt.
            ("Auth:MaxFailedLogins", "100"));
        var client = host.CreateClient();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var permitted = await LoginAsync(client, Email, WrongPassword, "device-a", null);
            Assert.Equal(HttpStatusCode.Unauthorized, permitted.StatusCode);
        }

        var throttled = await LoginAsync(client, Email, WrongPassword, "device-a", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);
    }

    [Fact]
    public async Task ChangePassword_ReturnsAWorkingNewToken_AndKillsTheOldOne()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost();
        var oldToken = await LoggedInTokenAsync(host.CreateClient(), Email, "device-a");
        const string newPassword = "a whole new passphrase";

        var response = await Authorized(host, oldToken).PostAsJsonAsync(
            "/auth/password", new ChangePasswordRequest(Password, newPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotEqual(oldToken, body!.Token);
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, body.Token).GetAsync("/auth/whoami")).StatusCode);

        // The stamp bump revokes every outstanding token, and the eviction makes it immediate rather
        // than lagging Auth:StampCacheSeconds behind.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Authorized(host, oldToken).GetAsync("/auth/whoami")).StatusCode);

        // The new password is the one that works from now on.
        var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, Email, Password, "device-a", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, Email, newPassword, "device-a", null)).StatusCode);
    }

    /// <summary>
    /// A stolen token must not be enough to take the account over, so the current password is re-verified
    /// even though the caller is already authenticated.
    /// </summary>
    [Fact]
    public async Task ChangePassword_WithTheWrongCurrentPassword_Returns401AndChangesNothing()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost();
        var token = await LoggedInTokenAsync(host.CreateClient(), Email, "device-a");
        var storedBefore = (await ReloadUserAsync()).PasswordHash;

        var response = await Authorized(host, token).PostAsJsonAsync(
            "/auth/password", new ChangePasswordRequest(WrongPassword, "a whole new passphrase"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var after = await ReloadUserAsync();
        Assert.Equal(storedBefore, after.PasswordHash);
        // The caller's own token survives, because nothing was rotated.
        Assert.Equal(HttpStatusCode.OK, (await Authorized(host, token).GetAsync("/auth/whoami")).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithATooShortNewPassword_Returns400()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost();
        var token = await LoggedInTokenAsync(host.CreateClient(), Email, "device-a");

        var response = await Authorized(host, token).PostAsJsonAsync(
            "/auth/password", new ChangePasswordRequest(Password, "short"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at least 12", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChangePassword_WithoutAToken_Returns401()
    {
        await SeedUserAsync();
        using var host = fixture.CreateHost();

        var response = await host.CreateClient().PostAsJsonAsync(
            "/auth/password", new ChangePasswordRequest(Password, "a whole new passphrase"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private WebApplicationFactory<Program> LockoutHost() => fixture.CreateHost(
        ("Auth:MaxFailedLogins", MaxFailedLogins),
        ("Auth:LockoutMinutes", LockoutMinutes),
        // The lockout tests spend more requests than the default limit allows, and the limiter is not
        // what they are asserting on.
        ("Auth:LoginRateLimitPerMinute", "100"));

    /// <summary>Burns the counter down to the threshold, leaving the account locked.</summary>
    private async Task LockAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < int.Parse(MaxFailedLogins); attempt++)
        {
            await LoginAsync(client, Email, WrongPassword, "device-a", null);
        }

        Assert.NotNull((await ReloadUserAsync()).LockoutEndUtc);
    }

    private static Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string email, string password, string deviceId, string? deviceLabel) =>
        client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password, deviceId, deviceLabel));

    private async Task<string> LoggedInTokenAsync(HttpClient client, string email, string deviceId)
    {
        var response = await LoginAsync(client, email, Password, deviceId, null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<SyncPullResponse> PullAsync(WebApplicationFactory<Program> host, string token) =>
        (await Authorized(host, token).GetFromJsonAsync<SyncPullResponse>("/sync/pull"))!;

    private async Task<int> SeedUserAsync(
        string email = Email, string? displayName = null, string? passwordHash = null)
    {
        using var db = fixture.CreateDbContext();
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = passwordHash ?? new PasswordService().Hash(Password),
            DisplayName = displayName,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>The pre-S6 legacy tenant: a real row with real data and no password. See <c>User</c>.</summary>
    private async Task SeedUnclaimedUserAsync()
    {
        using var db = fixture.CreateDbContext();
        db.Users.Add(new User
        {
            Email = "legacy@example.com",
            NormalizedEmail = "LEGACY@EXAMPLE.COM",
            PasswordHash = null,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedProjectAsync(int userId, string name)
    {
        using var db = fixture.CreateDbContext();
        var syncId = Guid.NewGuid();
        db.Projects.Add(new Project
        {
            UserId = userId,
            SyncId = syncId,
            Version = 1,
            Name = name,
            OriginDeviceId = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return syncId;
    }

    private async Task<User> ReloadUserAsync()
    {
        using var db = fixture.CreateDbContext();
        return await db.Users.SingleAsync(u => u.NormalizedEmail == Email.ToUpperInvariant());
    }

    private async Task<Device> SingleDeviceAsync()
    {
        using var db = fixture.CreateDbContext();
        return await db.Devices.SingleAsync();
    }
}
