using System.Globalization;
using System.Text;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Web;

namespace WorkLedger.Server.Email;

/// <summary>
/// How the connection to the relay is secured. <c>Email:SmtpSecurity</c>; see <see cref="EmailOptions"/>.
/// </summary>
public enum EmailSecurity
{
    /// <summary>
    /// Connect in plaintext and <b>require</b> STARTTLS before anything else is sent. The default, and
    /// what port 587 means.
    /// </summary>
    StartTls,

    /// <summary>TLS from the first byte ("implicit TLS"), usually port 465.</summary>
    SslOnConnect,

    /// <summary>
    /// No encryption at all. Only for a relay on the same host or compose network, e.g. a local Postfix,
    /// and refused at startup together with a username.
    /// </summary>
    None,
}

/// <summary>
/// The outbound mail settings (S12), resolved once and eagerly at startup next to <see cref="WebOptions"/>,
/// so a misconfigured relay exits the container naming the key.
/// <para>
/// <b>Off by default, and off means absent</b>, exactly as <c>Web:Enabled</c> is: with
/// <see cref="Enabled"/> false nothing else is read or validated, the delivery worker returns at once, and
/// no page offers to send anything. An upgraded deployment behaves exactly as it did before S12 until its
/// operator turns email on.
/// </para>
/// <para>
/// <b>Email requires the portal.</b> Every link a message carries goes to a portal page and is built by
/// <see cref="PortalLinks"/> from <c>Web:PublicBaseUrl</c>, which is only set while the portal is on. So
/// <c>Email:Enabled</c> with the portal off is a startup error that names both keys, rather than a message
/// that fails to render the first time someone needs it.
/// </para>
/// <para>
/// <b><see cref="EmailSecurity.StartTls"/> means MailKit's <c>SecureSocketOptions.StartTls</c>, and NEVER
/// <c>StartTlsWhenAvailable</c> or <c>Auto</c>.</b> Those two send in plaintext whenever the server does
/// not advertise STARTTLS — and the advertisement is itself sent in plaintext, so anyone between this
/// server and the relay can strip it and read every reset link and the SMTP password. Requiring it means a
/// relay without STARTTLS fails delivery loudly instead. "Use <c>Auto</c>, it's more compatible" is exactly
/// the change that turns this into a credential leak; don't make it.
/// </para>
/// <para>
/// <b>No setting disables certificate validation, and there will never be one.</b> A relay with a
/// self-signed certificate is handled by mounting its CA into the container's trust store, which the
/// README describes.
/// </para>
/// <para>
/// <b>Nothing here contacts the relay.</b> A mail server down for maintenance must not stop the container,
/// because that would stop sync for every device too. Configuration is checked at startup; delivery is
/// checked on demand, from the portal's test-message page.
/// </para>
/// <para>
/// A <b>blank</b> value counts as unset throughout, for the reason <c>PostgresConnection</c> and
/// <c>AuthOptions</c> give: compose renders an unset optional override as <c>""</c>.
/// </para>
/// </summary>
/// <param name="Enabled"><c>Email:Enabled</c>. Default false.</param>
/// <param name="SmtpHost"><c>Email:SmtpHost</c>. Required when enabled.</param>
/// <param name="SmtpPort"><c>Email:SmtpPort</c>. Default 587.</param>
/// <param name="SmtpSecurity"><c>Email:SmtpSecurity</c>. Default <see cref="EmailSecurity.StartTls"/>.</param>
/// <param name="SmtpUsername">
/// <c>Email:SmtpUsername</c>. Optional: some relays authenticate by the sender's IP address.
/// </param>
/// <param name="SmtpPassword">
/// <c>Email:SmtpPassword</c>, <b>a secret</b>, required when a username is set. Never logged, never shown on
/// a page, and left out of this record's <see cref="object.ToString"/>.
/// </param>
/// <param name="FromAddress">
/// <c>Email:FromAddress</c>. Required when enabled. It is the operator's own sending address rather than a
/// user's, which is why the startup line may name it.
/// </param>
/// <param name="FromName"><c>Email:FromName</c>. Default <c>WorkLedger</c>.</param>
/// <param name="QueueCapacity">
/// <c>Email:QueueCapacity</c>: how many messages <see cref="EmailOutbox"/> holds before it refuses more.
/// Default 100.
/// </param>
/// <param name="ResetCooldownMinutes">
/// <c>Email:ResetCooldownMinutes</c>: after a self-service reset is issued for a user, how long further
/// <c>/portal/forgot</c> requests for that user send nothing. Default 5; <c>0</c> turns the cooldown off.
/// See <c>SelfServiceRecovery</c> for why it exists.
/// </param>
public sealed record EmailOptions(
    bool Enabled,
    string? SmtpHost,
    int SmtpPort,
    EmailSecurity SmtpSecurity,
    string? SmtpUsername,
    string? SmtpPassword,
    string? FromAddress,
    string FromName,
    int QueueCapacity,
    int ResetCooldownMinutes = EmailOptions.DefaultResetCooldownMinutes)
{
    public const int DefaultSmtpPort = 587;
    public const string DefaultFromName = "WorkLedger";
    public const int DefaultQueueCapacity = 100;
    public const int DefaultResetCooldownMinutes = 5;

    public TimeSpan ResetCooldown => TimeSpan.FromMinutes(ResetCooldownMinutes);

    /// <summary>The state of a deployment that never turned email on.</summary>
    public static EmailOptions Disabled { get; } = new(
        false, null, DefaultSmtpPort, EmailSecurity.StartTls, null, null, null, DefaultFromName, DefaultQueueCapacity);

    /// <summary>
    /// The startup line, in both modes, so "I never turned it on" and "I turned it on and nothing arrives"
    /// can be told apart from <c>docker compose logs</c> — the same reason <c>TrustedProxies</c> logs its
    /// mode. Host, port and from address are not secret; the username and password are never part of it.
    /// </summary>
    public string Describe()
    {
        if (!Enabled)
        {
            return "Email off: invite and reset links are handed over by an admin.";
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"Email on: relaying via {SmtpHost}:{SmtpPort} ({SmtpSecurity}) as {FromAddress}.");
        return SmtpSecurity == EmailSecurity.None
            ? line + " Email:SmtpSecurity is None, so mail leaves this process unencrypted."
            : line;
    }

    public static EmailOptions Resolve(IConfiguration configuration, WebOptions web)
    {
        var enabledValue = Configured(configuration, "Email:Enabled");
        if (enabledValue is null || !bool.TryParse(enabledValue, out var enabled) || !enabled)
        {
            // Nothing else is read or validated while email is off: an operator who has not turned it on
            // must not be refused startup over a key they have never heard of.
            return Disabled;
        }

        if (!web.Enabled)
        {
            throw new InvalidOperationException(
                "Email:Enabled is true but Web:Enabled is not. Every link an email carries goes to a portal " +
                "page built from Web:PublicBaseUrl, so turn the portal on (Web:Enabled and " +
                "Web:PublicBaseUrl) or turn Email:Enabled off.");
        }

        var host = Configured(configuration, "Email:SmtpHost")
            ?? throw new InvalidOperationException(
                "Email:SmtpHost (or Email__SmtpHost env var) is required when Email:Enabled is true — the " +
                "relay to send through, e.g. smtp.resend.com.");

        var port = Number(configuration, "Email:SmtpPort", DefaultSmtpPort);
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Email:SmtpPort must be a port number between 1 and 65535 (or unset, for {DefaultSmtpPort}).");
        }

        var security = Security(Configured(configuration, "Email:SmtpSecurity"));
        var username = Configured(configuration, "Email:SmtpUsername");

        // The password is taken as given, untrimmed: it is a secret, and a trailing space in one is a
        // character the relay issued rather than a typo to repair. Blank still counts as unset.
        var password = configuration["Email:SmtpPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            password = null;
        }

        if (username is not null && password is null)
        {
            throw new InvalidOperationException(
                "Email:SmtpPassword (or Email__SmtpPassword env var) is required when Email:SmtpUsername is " +
                "set. Leave both unset only for a relay that authenticates by IP address.");
        }

        if (username is not null && security == EmailSecurity.None)
        {
            throw new InvalidOperationException(
                "Email:SmtpSecurity is None but Email:SmtpUsername is set, which would send the SMTP " +
                "credentials in plaintext. Use StartTls or SslOnConnect, or drop the username for a local " +
                "relay that needs none.");
        }

        var from = Configured(configuration, "Email:FromAddress")
            ?? throw new InvalidOperationException(
                "Email:FromAddress (or Email__FromAddress env var) is required when Email:Enabled is true — " +
                "an address on a domain the relay is allowed to send for.");
        if (!Credentials.LooksLikeEmail(from))
        {
            throw new InvalidOperationException(
                $"Email:FromAddress must contain a single '@' with text on both sides of it. Got '{from}'.");
        }

        var capacity = Number(configuration, "Email:QueueCapacity", DefaultQueueCapacity);
        if (capacity <= 0)
        {
            throw new InvalidOperationException(
                "Email:QueueCapacity must be a whole number greater than zero (or unset, for " +
                $"{DefaultQueueCapacity}).");
        }

        var cooldown = Number(configuration, "Email:ResetCooldownMinutes", DefaultResetCooldownMinutes);
        if (cooldown < 0)
        {
            throw new InvalidOperationException(
                "Email:ResetCooldownMinutes must be a whole number of minutes, zero or more (or unset, for " +
                $"{DefaultResetCooldownMinutes}). Zero turns the self-service reset cooldown off.");
        }

        return new EmailOptions(
            Enabled: true,
            SmtpHost: host,
            SmtpPort: port,
            SmtpSecurity: security,
            SmtpUsername: username,
            SmtpPassword: password,
            FromAddress: from,
            FromName: Configured(configuration, "Email:FromName") ?? DefaultFromName,
            QueueCapacity: capacity,
            ResetCooldownMinutes: cooldown);
    }

    /// <summary>
    /// Leaves <see cref="SmtpPassword"/> out of the compiler-generated <c>ToString</c>, so an options
    /// object that ends up in a log line or an exception message does not take the secret with it.
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Enabled = {Enabled}, SmtpHost = {SmtpHost}, SmtpPort = {SmtpPort}, ");
        builder.Append(CultureInfo.InvariantCulture, $"SmtpSecurity = {SmtpSecurity}, SmtpUsername = {SmtpUsername}, ");
        builder.Append(CultureInfo.InvariantCulture, $"SmtpPassword = {(SmtpPassword is null ? "(unset)" : "(set)")}, ");
        builder.Append(CultureInfo.InvariantCulture, $"FromAddress = {FromAddress}, FromName = {FromName}, QueueCapacity = {QueueCapacity}, ResetCooldownMinutes = {ResetCooldownMinutes}");
        return true;
    }

    /// <summary>
    /// The three names, compared ignoring case. Anything else — including a number, which
    /// <c>Enum.TryParse</c> would otherwise accept — fails startup rather than falling back to a default,
    /// because a misspelled <c>SslOnConect</c> silently becoming <c>StartTls</c> would only show up as a
    /// relay that never answers.
    /// </summary>
    private static EmailSecurity Security(string? value)
    {
        if (value is null)
        {
            return EmailSecurity.StartTls;
        }

        foreach (var security in Enum.GetValues<EmailSecurity>())
        {
            if (string.Equals(value, security.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return security;
            }
        }

        throw new InvalidOperationException(
            $"Email:SmtpSecurity must be StartTls, SslOnConnect or None (or unset, for StartTls). Got '{value}'.");
    }

    /// <summary>
    /// <c>InvariantCulture</c>, for the reason every parse here pins it (the container runs
    /// invariant-globalization). An unparsable number fails startup rather than taking the default: a
    /// port that silently became 587 is a relay that never answers.
    /// </summary>
    private static int Number(IConfiguration configuration, string key, int @default)
    {
        var value = Configured(configuration, key);
        if (value is null)
        {
            return @default;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{key} must be a whole number. Got '{value}'.");
    }

    private static string? Configured(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
