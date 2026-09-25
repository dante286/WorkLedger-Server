using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;
using WorkLedger.Server.Tests.Auth;

namespace WorkLedger.Server.Tests.Web;

/// <summary>
/// The portal's hosting contract (S11 items 5, 8 and 9): off by default and then absent, fail-fast on a
/// bad <c>Web:PublicBaseUrl</c>, <c>GET /</c> negotiation, the persisted key ring, and the headers portal
/// responses carry that JSON responses must not.
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public class PortalHostingTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// With the portal off, <c>/portal/*</c> is simply not a route: a signed-in device gets the same 404
    /// any unknown path gives it, and an anonymous caller the same 401 the fallback policy gives any
    /// unknown path — nothing distinguishes the portal's paths from paths that never existed.
    /// </summary>
    [Fact]
    public async Task PortalOff_PortalPathsAreNotRoutes()
    {
        await SeedUserAsync("sam@example.com");
        using var host = fixture.CreateHost();
        var token = await DeviceTokenAsync(host, "sam@example.com");

        var anonymous = host.CreateClient();
        var device = host.CreateClient();
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        foreach (var path in new[] { "/portal", "/portal/login", "/portal/assets/portal.css" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await device.GetAsync(path)).StatusCode);
            Assert.Equal(
                (await anonymous.GetAsync("/no-such-route")).StatusCode,
                (await anonymous.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task PortalOff_RootIsJsonWhateverTheAcceptHeader()
    {
        using var host = fixture.CreateHost();
        var client = host.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("WorkLedger.Server", body.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task PortalOff_KeyRingIsNotPersistedAndNoHeadersAreAdded()
    {
        using var host = fixture.CreateHost();
        var response = await host.CreateClient().GetAsync("/health");

        var keyManagement = host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        Assert.IsNotType<EntityFrameworkCoreXmlRepository<WorkLedgerDbContext>>(keyManagement.XmlRepository);
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
        Assert.False(response.Headers.Contains("Referrer-Policy"));
    }

    [Fact]
    public void PortalOn_WithoutAPublicBaseUrl_RefusesToStart_NamingTheKey()
    {
        using var host = fixture.CreateHost(("Web:Enabled", "true"));

        var error = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains("Web:PublicBaseUrl", error.Message);
    }

    [Theory]
    [InlineData("ledger.example.com")]
    [InlineData("/portal")]
    [InlineData("http://ledger.example.com")]
    [InlineData("https://ledger.example.com/?next=x")]
    [InlineData("ftp://ledger.example.com")]
    public void PortalOn_WithAnUnusablePublicBaseUrl_RefusesToStart(string value)
    {
        using var host = fixture.CreateHost(("Web:Enabled", "true"), ("Web:PublicBaseUrl", value));

        var error = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains("Web:PublicBaseUrl", error.Message);
    }

    /// <summary>
    /// Plain http is allowed for a loopback address only — that is development, and browsers treat it as a
    /// secure context, so the Secure cookie still works there.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:5284")]
    [InlineData("http://127.0.0.1:5284")]
    [InlineData("https://ledger.example.com/")]
    public async Task PortalOn_WithAUsablePublicBaseUrl_Starts(string value)
    {
        using var host = fixture.CreateHost(("Web:Enabled", "true"), ("Web:PublicBaseUrl", value));

        var response = await host.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PortalOn_RootSendsBrowsersToThePortal_AndEveryoneElseGetsJson()
    {
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);

        using var navigation = new HttpRequestMessage(HttpMethod.Get, "/");
        navigation.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
        var redirected = await browser.Client.SendAsync(navigation);

        Assert.Equal(HttpStatusCode.Redirect, redirected.StatusCode);
        Assert.Equal("/portal", redirected.Headers.Location!.OriginalString);

        foreach (var accept in new[] { "application/json", "*/*" })
        {
            using var api = new HttpRequestMessage(HttpMethod.Get, "/");
            api.Headers.Accept.ParseAdd(accept);
            var response = await browser.Client.SendAsync(api);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        }
    }

    [Fact]
    public async Task PortalResponses_CarryTheHardeningHeaders_AndJsonResponsesDoNot()
    {
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);

        var page = await browser.GetAsync("/portal/login");
        var json = await browser.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(
            "default-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'; object-src 'none'",
            Assert.Single(page.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("no-referrer", Assert.Single(page.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("nosniff", Assert.Single(page.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(page.Headers.CacheControl?.NoStore);

        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        Assert.False(json.Headers.Contains("Content-Security-Policy"));
        Assert.False(json.Headers.Contains("Referrer-Policy"));
        Assert.False(json.Headers.Contains("X-Content-Type-Options"));
    }

    /// <summary>
    /// The CSP allows no inline script, so this is also the check that nothing on the page needed any:
    /// everything it loads is one of these two files, served anonymously.
    /// </summary>
    [Fact]
    public async Task StaticAssets_AreServedAnonymously_AndPagesUseNoInlineScriptOrStyle()
    {
        using var host = PortalHost();
        var browser = PortalBrowser.Open(host);

        var css = await browser.GetAsync("/portal/assets/portal.css");
        var js = await browser.GetAsync("/portal/assets/portal.js");
        var html = await (await browser.GetAsync("/portal/login")).Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain("style=", html);
    }

    /// <summary>
    /// The deployment the README describes: TLS ends at a reverse proxy, so this process sees every
    /// request as plain http, and <c>X-Forwarded-Proto</c> is deliberately not trusted to say otherwise.
    /// The browser's side is HTTPS, so it accepts the Secure cookies and sends them back; the proxy
    /// forwards them. Both cookies must still come out Secure, and nothing may refuse to run for want of
    /// an HTTPS request — antiforgery's own check did exactly that until the portal stopped using it.
    /// </summary>
    [Fact]
    public async Task BehindATlsProxy_WhereThisProcessSeesPlainHttp_SignInWorks()
    {
        await SeedUserAsync("sam@example.com");
        using var host = PortalHost();
        var hop = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        var form = await hop.GetAsync("/portal/login");
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        var antiforgery = SetCookie(form, "__Host-WorkLedger-Antiforgery=");
        Assert.Contains("secure", antiforgery.ToLowerInvariant());

        using var post = new HttpRequestMessage(HttpMethod.Post, "/portal/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Email"] = "sam@example.com",
                ["Password"] = Password,
                ["__RequestVerificationToken"] = PortalBrowser.TokenFrom(await form.Content.ReadAsStringAsync()),
            }),
        };
        post.Headers.Add("Cookie", antiforgery.Split(';')[0]);
        var signIn = await hop.SendAsync(post);

        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        var session = SetCookie(signIn, "__Host-WorkLedger=");
        Assert.Contains("secure", session.ToLowerInvariant());

        using var home = new HttpRequestMessage(HttpMethod.Get, "/portal");
        home.Headers.Add("Cookie", session.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await hop.SendAsync(home)).StatusCode);
    }

    /// <summary>
    /// Two hosts over one database stand in for a restart, an image upgrade, or a second replica: the
    /// session one issued has to be readable by the other, which only holds if the key ring lives in
    /// Postgres rather than in either process's filesystem or memory.
    /// </summary>
    [Fact]
    public async Task ASessionIssuedByOneHost_IsAcceptedByAnother()
    {
        await SeedUserAsync("sam@example.com");
        using var first = PortalHost();
        using var second = PortalHost();

        var signIn = await PortalBrowser.Open(first).SignInAsync("sam@example.com", Password);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        var sessionCookie = signIn.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith("__Host-WorkLedger=", StringComparison.Ordinal))
            .Split(';')[0];

        var elsewhere = second.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/portal");
        request.Headers.Add("Cookie", sessionCookie);
        var response = await elsewhere.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var db = fixture.CreateDbContext();
        Assert.NotEmpty(db.DataProtectionKeys);
    }

    private static string SetCookie(HttpResponseMessage response, string prefix) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(prefix, StringComparison.Ordinal));

    private WebApplicationFactory<Program> PortalHost() =>
        fixture.CreateHost(
            ("Web:Enabled", "true"),
            ("Web:PublicBaseUrl", "https://ledger.example.test"),
            ("Auth:LoginRateLimitPerMinute", "200"));

    private async Task SeedUserAsync(string email)
    {
        using var db = fixture.CreateDbContext();
        db.Users.Add(new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = new PasswordService().Hash(Password),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<string> DeviceTokenAsync(WebApplicationFactory<Program> host, string email)
    {
        var response = await host.CreateClient()
            .PostAsJsonAsync("/auth/login", new LoginRequest(email, Password, "device-a", null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }
}
