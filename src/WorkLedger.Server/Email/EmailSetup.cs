using WorkLedger.Server.Web;

namespace WorkLedger.Server.Email;

/// <summary>
/// Wires outbound mail (S12) into the host.
/// <para>
/// <b>With <c>Email:Enabled</c> off there is no mail worker.</b> <see cref="EmailOptions.Resolve"/> reads no
/// other <c>Email:*</c> key, <see cref="EmailDeliveryService"/> is never constructed or started,
/// <see cref="EmailOutbox"/> has no channel, and nothing calls <see cref="IEmailSender"/>.
/// </para>
/// <para>
/// <b>Why the decision is made in a factory rather than around the registration.</b>
/// <c>WebApplicationFactory</c> applies configuration only when <c>Build()</c> runs (see the note at the top
/// of <c>Program.cs</c> and <c>PortalSetup</c>), so an <c>if</c> on the flag here would read a different
/// value in the tests than in production. Hosted services are resolved when the host starts, after
/// <c>Build()</c>, so the factory below sees the real <see cref="EmailOptions"/>. A service collection cannot
/// register "nothing" conditionally, so with email off the factory hands back <see cref="EmailOff"/>, an
/// empty placeholder that starts nothing.
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
        services.AddScoped<RelayCheck>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<EmailOptions>().Enabled
            ? ActivatorUtilities.CreateInstance<EmailDeliveryService>(sp)
            : EmailOff.Instance);
    }

    /// <summary>The hosted-service slot's occupant while email is off: no thread, no loop, no queue.</summary>
    private sealed class EmailOff : IHostedService
    {
        public static readonly EmailOff Instance = new();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
