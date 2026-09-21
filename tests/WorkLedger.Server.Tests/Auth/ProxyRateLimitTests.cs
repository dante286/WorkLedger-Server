using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WorkLedger.Contracts.Auth;

namespace WorkLedger.Server.Tests.Auth;

/// <summary>
/// What <c>Network:TrustedProxies</c> actually changes: which address the login rate limiter partitions
/// on. The parsing is covered by <c>TrustedProxiesTests</c>; this is the behaviour.
/// <para>
/// <b>Every test here exhausts a limiter, so each builds its own host</b> — a shared one would carry a
/// poisoned bucket into the next test. The limit is set to 2/minute so three requests settle it, and the
/// window is left at a real minute so nothing can expire mid-test.
/// </para>
/// <para>
/// <b>Why the <c>IStartupFilter</c>.</b> <see cref="WebApplicationFactory{TEntryPoint}"/> serves over an
/// in-memory transport, so <c>Connection.RemoteIpAddress</c> is null and the forwarded-headers
/// middleware — which compares it against the known-proxy list — would never trust anything. The filter
/// sets it from a test-only header, and it has to be a startup filter rather than ordinary middleware
/// because it must run <i>before</i> the pipeline <c>Program.cs</c> builds.
/// </para>
/// </summary>
[Collection(nameof(UserAccountsCollection))]
public class ProxyRateLimitTests(UserAccountsFixture fixture) : IAsyncLifetime
{
    /// <summary>The socket address every request in this suite appears to come from.</summary>
    private const string ProxyAddress = "10.0.0.1";

    private const string ClientA = "203.0.113.10";
    private const string ClientB = "203.0.113.20";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The default, and the pre-S10D behaviour exactly: <c>X-Forwarded-For</c> is not read, so two
    /// callers behind one proxy share a bucket. This is the bug the setting exists to let an operator
    /// fix — and the test that proves leaving it unset changes nothing.
    /// </summary>
    [Fact]
    public async Task WithNoTrustedProxies_ForwardedAddressesShareOneBucket()
    {
        using var host = Host(trustedProxies: null);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientA));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientA));

        // A different forwarded address, and still refused — the limiter never saw it.
        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientB));
    }

    /// <summary>
    /// The fix: with the proxy named, each caller behind it gets its own bucket, so one person signing
    /// in repeatedly cannot lock everybody else out.
    /// </summary>
    [Fact]
    public async Task WithTheProxyTrusted_EachForwardedAddressGetsItsOwnBucket()
    {
        using var host = Host(trustedProxies: ProxyAddress);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientA));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientA));

        // B is untouched by A having exhausted its own allowance...
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientB));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientB));

        // ...and A is still limited, so this is two buckets rather than one larger one.
        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientA));
        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientB));
    }

    /// <summary>
    /// The security property, and the reason the list is not a blanket "trust forwarded headers": a
    /// header arriving from a hop that is <i>not</i> on the list is ignored. Without this, anyone could
    /// mint an address per request and give themselves an unlimited bucket — turning the limiter off for
    /// precisely the caller it exists to stop.
    /// </summary>
    [Fact]
    public async Task WithADifferentProxyTrusted_TheForwardedHeaderIsIgnored()
    {
        using var host = Host(trustedProxies: "192.168.50.1");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientA));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientB));

        // Two different forwarded addresses, one real socket, one bucket — exhausted.
        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptAsync(host, "203.0.113.30"));
    }

    /// <summary>A CIDR block covering the proxy works the same as naming it outright.</summary>
    [Fact]
    public async Task ACidrBlockCoveringTheProxy_TrustsItToo()
    {
        using var host = Host(trustedProxies: "10.0.0.0/24");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientA));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientA));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientB));
        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptAsync(host, ClientA));
    }

    /// <summary>
    /// A misspelled entry takes the host down at startup rather than being skipped — see
    /// <c>TrustedProxies</c> for why silently ignoring it is the worse outcome.
    /// </summary>
    [Fact]
    public void AMalformedTrustedProxiesValue_FailsStartup()
    {
        using var host = Host(trustedProxies: "10.0.0.1, nope");

        var exception = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains("nope", exception.Message);
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// One login attempt for an account that does not exist, as <paramref name="forwardedFor"/>. The
    /// credentials are irrelevant — what is being measured is whether the limiter admitted the request
    /// at all, so the interesting answers are "429" and "anything else".
    /// </summary>
    private static async Task<HttpStatusCode> AttemptAsync(
        WebApplicationFactory<Program> host, string forwardedFor)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote-Ip", ProxyAddress);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);

        var response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest("nobody@example.com", "irrelevant", "probe-device", null));
        return response.StatusCode;
    }

    private WebApplicationFactory<Program> Host(string? trustedProxies)
    {
        var settings = TestConfig.RequiredStartupSettings();
        settings["ConnectionStrings:Postgres"] = fixture.ConnectionString;
        settings["Database:MigrateOnStartup"] = "false";
        settings["Auth:LoginRateLimitPerMinute"] = "2";
        if (trustedProxies is not null)
        {
            settings["Network:TrustedProxies"] = trustedProxies;
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter, RemoteIpFromTestHeader>());
        });
    }

    /// <summary>
    /// Gives the request the socket address it would have had over a real connection. Registered as a
    /// startup filter so it runs ahead of everything <c>Program.cs</c> maps — including the
    /// forwarded-headers middleware, which is the whole point.
    /// </summary>
    private sealed class RemoteIpFromTestHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Remote-Ip", out var value) &&
                    IPAddress.TryParse(value.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await nextMiddleware();
            });

            next(app);
        };
    }
}
