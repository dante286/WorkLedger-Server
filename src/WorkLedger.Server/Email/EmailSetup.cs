using WorkLedger.Server.Web;

namespace WorkLedger.Server.Email;

/// <summary>
/// Wires outbound mail (S12) into the host.
/// <para>
/// <b>Registered unconditionally and inert while <c>Email:Enabled</c> is off</b>, for the reason
/// <c>PortalSetup</c> gives: <c>WebApplicationFactory</c> applies configuration only when <c>Build()</c>
/// runs, so a registration that depended on the flag would make the tests a different app from
/// production. With email off, <see cref="EmailDeliveryService"/> returns before reading anything,
/// <see cref="EmailOutbox"/> has no channel, and nothing calls <see cref="IEmailSender"/>.
/// </para>
/// </summary>
internal static class EmailSetup
{
    public static void AddEmail(this IServiceCollection services)
    {
        services.AddSingleton(sp => EmailOptions.Resolve(
            sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<WebOptions>()));
        services.AddSingleton<EmailOutbox>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddHostedService<EmailDeliveryService>();
    }
}
