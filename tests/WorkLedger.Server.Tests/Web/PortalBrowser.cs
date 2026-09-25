using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WorkLedger.Server.Tests.Web;

/// <summary>
/// A client that behaves enough like a browser for the portal: it keeps cookies, talks to
/// <c>https://localhost</c> so the <c>Secure</c>, <c>__Host-</c> session cookie round-trips at all, does
/// NOT follow redirects (the tests assert on where they point), and posts forms with the antiforgery token
/// scraped from the page that rendered them — exactly what a browser submitting that form would send.
/// </summary>
internal sealed partial class PortalBrowser(HttpClient client)
{
    public HttpClient Client { get; } = client;

    public static PortalBrowser Open(WebApplicationFactory<Program> host) =>
        new(host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        }));

    public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(path);

    /// <summary>
    /// Loads <paramref name="formPage"/> for its antiforgery token, then posts the form to it. A
    /// <c>?handler=</c> query names which form on the page is being submitted, so it is dropped for the
    /// load and kept for the post.
    /// </summary>
    public async Task<HttpResponseMessage> SubmitAsync(string formPage, params (string Name, string? Value)[] fields)
    {
        var token = await FormTokenAsync(formPage.Contains("?handler=") ? formPage[..formPage.IndexOf('?')] : formPage);
        return await PostAsync(formPage, token, fields);
    }

    /// <summary>
    /// Posts form fields to <paramref name="path"/>, with <paramref name="antiforgeryToken"/> if given —
    /// null is how the antiforgery tests submit a forged form.
    /// </summary>
    public Task<HttpResponseMessage> PostAsync(
        string path, string? antiforgeryToken, params (string Name, string? Value)[] fields)
    {
        var values = fields
            .Where(f => f.Value is not null)
            .Select(f => new KeyValuePair<string, string>(f.Name, f.Value!))
            .ToList();
        if (antiforgeryToken is not null)
        {
            values.Add(new("__RequestVerificationToken", antiforgeryToken));
        }

        return Client.PostAsync(path, new FormUrlEncodedContent(values));
    }

    public async Task<string> FormTokenAsync(string page) =>
        TokenFrom(await (await Client.GetAsync(page)).Content.ReadAsStringAsync());

    /// <summary>The antiforgery token a rendered form carries.</summary>
    public static string TokenFrom(string html)
    {
        var match = AntiforgeryField().Match(html);
        Assert.True(match.Success, "No antiforgery token on the page.");
        return match.Groups[1].Value;
    }

    public Task<HttpResponseMessage> SignInAsync(string email, string password, string? returnUrl = null) =>
        SubmitAsync(
            returnUrl is null ? "/portal/login" : $"/portal/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}",
            ("Email", email),
            ("Password", password));

    /// <summary>Signs out through the button every signed-in page carries in its header.</summary>
    public async Task<HttpResponseMessage> SignOutAsync() =>
        await PostAsync("/portal/logout", await FormTokenAsync("/portal"));

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();
}
