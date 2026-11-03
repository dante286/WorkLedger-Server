using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using WorkLedger.Server.Email;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Tests.Email;

/// <summary>
/// The only tests that run MailKit for real (S12 item 20). Every other email test swaps
/// <see cref="IEmailSender"/> for <see cref="RecordingEmailSender"/>; these point the real
/// <see cref="SmtpEmailSender"/> at a Mailpit container and read what arrived back through its HTTP API.
/// </summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    /// <summary>Pinned, not <c>latest</c>: an image that changes under the suite is a flaky suite.</summary>
    public const string Image = "axllent/mailpit:v1.33.0";

    private const int SmtpPort = 1025;
    private const int HttpPort = 8025;

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithPortBinding(SmtpPort, assignRandomHostPort: true)
        .WithPortBinding(HttpPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(HttpPort).ForPath("/api/v1/info")))
        .Build();

    public string Host => _container.Hostname;

    public int Smtp => _container.GetMappedPublicPort(SmtpPort);

    public HttpClient Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Api = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(HttpPort)}") };
    }

    public async Task DisposeAsync()
    {
        Api?.Dispose();
        await _container.DisposeAsync();
    }
}

public class MailpitTransportTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>, IAsyncLifetime
{
    private const string From = "no-reply@ledger.example.test";
    private const string Token = "AbC123-xyz_tokenValue";

    private static readonly PortalLinks Links =
        new(new WebOptions(true, "https://ledger.example.test", WebOptions.DefaultSessionHours));

    public async Task InitializeAsync() => await mailpit.Api.DeleteAsync("/api/v1/messages");

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// What a recipient actually receives: the address, the subject, a link with the token in its
    /// fragment, the same token on a line of its own, plain text only, and <c>Auto-Submitted</c>.
    /// </summary>
    [Fact]
    public async Task AnInvite_ArrivesAsPlainText_WithTheLink_TheBareToken_AndAutoSubmitted()
    {
        var sender = new SmtpEmailSender(Options(EmailSecurity.None));
        var content = EmailMessages.Invite(Links, Token, new DateTime(2026, 11, 17, 14, 0, 0, DateTimeKind.Utc));

        await sender.SendAsync(new OutgoingEmail(EmailKind.Invite, null, "invitee@example.test", content), CancellationToken.None);

        var message = await SingleMessageAsync();
        Assert.Equal("invitee@example.test", message.GetProperty("To")[0].GetProperty("Address").GetString());
        Assert.Equal(From, message.GetProperty("From").GetProperty("Address").GetString());
        Assert.Equal("You've been invited to WorkLedger", message.GetProperty("Subject").GetString());
        var text = message.GetProperty("Text").GetString()!;
        Assert.Contains($"https://ledger.example.test/portal/register#invite={Token}", text);
        Assert.Matches(new Regex($"(?m)^{Regex.Escape(Token)}\\r?$"), text);
        Assert.Equal(string.Empty, message.GetProperty("HTML").GetString());

        var headers = await HeadersAsync(message.GetProperty("ID").GetString()!);
        Assert.Equal("auto-generated", headers.GetProperty("Auto-Submitted")[0].GetString());
        Assert.StartsWith("text/plain", headers.GetProperty("Content-Type")[0].GetString());
    }

    /// <summary>
    /// The rule <c>EmailOptions</c> exists to keep: with <see cref="EmailSecurity.StartTls"/>, a relay that
    /// does not offer STARTTLS — Mailpit's plaintext port, standing in for an attacker who stripped the
    /// advertisement — fails delivery outright. Nothing is sent in plaintext instead.
    /// </summary>
    [Fact]
    public async Task StartTls_AgainstARelayWithoutIt_FailsRatherThanDowngrading()
    {
        var sender = new SmtpEmailSender(Options(EmailSecurity.StartTls));
        var content = EmailMessages.AdminPasswordReset(Links, Token, DateTime.UtcNow.AddHours(24));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync(
            new OutgoingEmail(EmailKind.AdminPasswordReset, 1, "sam@example.test", content), CancellationToken.None));

        Assert.IsType<NotSupportedException>(failure);
        Assert.Contains("STARTTLS", failure.Message);
        Assert.False(EmailDeliveryService.IsTransient(failure), "a missing STARTTLS must not be retried");
        await Task.Delay(200);
        Assert.Equal(0, (await MessagesAsync()).GetArrayLength());
    }

    /// <summary>
    /// A recipient on a non-ASCII domain, which <c>Credentials.LooksLikeEmail</c> accepts. On the host this
    /// runs with full globalization; the Alpine image runs invariant globalization, and the smoke test's
    /// <c>-WithMailpit</c> step is what checks the same address there.
    /// </summary>
    [Fact]
    public async Task ARecipientOnANonAsciiDomain_IsDelivered()
    {
        var sender = new SmtpEmailSender(Options(EmailSecurity.None));

        await sender.SendAsync(
            new OutgoingEmail(EmailKind.Test, null, "jose@bücher.example", EmailMessages.Test(Options(EmailSecurity.None))),
            CancellationToken.None);

        var to = (await SingleMessageAsync()).GetProperty("To")[0].GetProperty("Address").GetString()!;
        Assert.True(to is "jose@bücher.example" or "jose@xn--bcher-kva.example", to);
    }

    private EmailOptions Options(EmailSecurity security) => EmailOptions.Disabled with
    {
        Enabled = true,
        SmtpHost = mailpit.Host,
        SmtpPort = mailpit.Smtp,
        SmtpSecurity = security,
        FromAddress = From,
    };

    private async Task<JsonElement> MessagesAsync()
    {
        using var list = JsonDocument.Parse(await mailpit.Api.GetStringAsync("/api/v1/messages"));
        return list.RootElement.GetProperty("messages").Clone();
    }

    private async Task<JsonElement> SingleMessageAsync()
    {
        JsonElement messages = default;
        for (var i = 0; i < 50 && (messages.ValueKind == JsonValueKind.Undefined || messages.GetArrayLength() == 0); i++)
        {
            messages = await MessagesAsync();
            await Task.Delay(messages.GetArrayLength() == 0 ? 100 : 0);
        }

        var id = Assert.Single(messages.EnumerateArray()).GetProperty("ID").GetString();
        return (await mailpit.Api.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}"));
    }

    private async Task<JsonElement> HeadersAsync(string id) =>
        await mailpit.Api.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}/headers");
}
