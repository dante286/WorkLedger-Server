using System.Globalization;
using System.Net;

namespace WorkLedger.Server;

/// <summary>
/// The reverse proxies whose <c>X-Forwarded-For</c> this deployment believes, from
/// <c>Network:TrustedProxies</c> — a comma-separated list of IP addresses and CIDR blocks.
/// <para>
/// <b>Why this has to be configured rather than inferred.</b> The login rate limiter partitions on
/// <c>Connection.RemoteIpAddress</c>. Behind a proxy that is the proxy's address for every request, so
/// the whole deployment shares one bucket and a handful of people signing in at once lock each other
/// out with <c>429</c>s. The obvious fix — read <c>X-Forwarded-For</c> — is a much worse bug when the
/// hop is not trusted: the header is caller-supplied, so anyone could mint a fresh address per request
/// and give themselves an unlimited bucket, turning the limiter off for exactly the attacker it exists
/// to stop. There is no way to tell the two situations apart from inside the process. Only the operator
/// knows what sits in front of this server, so only the operator can say.
/// </para>
/// <para>
/// <b>Unset is the default, and means today's behaviour exactly</b> — no forwarded-header processing at
/// all, partitioning on the socket address. An operator running this without a proxy needs to know
/// nothing about any of it.
/// </para>
/// <para>
/// <b>A bad entry fails startup rather than being skipped.</b> Silently ignoring a typo'd address would
/// leave an operator believing they had configured the thing they just misspelled, and the symptom —
/// everyone sharing a bucket again — looks identical to not having configured it at all. Failing with
/// the offending value named is the only outcome that is diagnosable from <c>docker compose logs</c>.
/// </para>
/// </summary>
public sealed record TrustedProxies(
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyList<IPNetwork> Networks)
{
    public const string ConfigurationKey = "Network:TrustedProxies";

    /// <summary>Whether the operator named anything at all. False means "leave the pipeline alone".</summary>
    public bool IsConfigured => Addresses.Count > 0 || Networks.Count > 0;

    /// <summary>
    /// The startup log line's subject. An operator debugging "everyone is getting 429" needs to see
    /// which of the two modes they are actually in, and against which hops.
    /// </summary>
    public string Describe() => IsConfigured
        ? string.Join(", ", Addresses.Select(a => a.ToString()).Concat(Networks.Select(n => n.ToString())))
        : "(none)";

    public static TrustedProxies Resolve(IConfiguration configuration)
    {
        var configured = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new TrustedProxies([], []);
        }

        var addresses = new List<IPAddress>();
        var networks = new List<IPNetwork>();

        foreach (var entry in configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Contains('/'))
            {
                networks.Add(ParseNetwork(entry));
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                addresses.Add(address);
            }
            else
            {
                throw new InvalidOperationException(Invalid(entry));
            }
        }

        return new TrustedProxies(addresses, networks);
    }

    /// <summary>
    /// <c>IPNetwork.Parse</c>, with its exception turned into the same startup failure a bad address
    /// gets. What survives to here is a genuinely malformed block — a prefix length out of range, or
    /// something that is not CIDR at all.
    /// <para>
    /// <b>Host bits set are not an error</b>, which is worth knowing before anyone adds a check for
    /// them: <c>Parse</c> normalizes <c>10.0.0.5/8</c> to <c>10.0.0.0/8</c> and matching then behaves
    /// exactly as the operator intended. Rejecting it would be inventing a rule the platform does not
    /// have, to refuse input that already works.
    /// </para>
    /// </summary>
    private static IPNetwork ParseNetwork(string entry)
    {
        try
        {
            return IPNetwork.Parse(entry);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new InvalidOperationException(Invalid(entry), exception);
        }
    }

    private static string Invalid(string entry) => string.Format(
        CultureInfo.InvariantCulture,
        "{0} contains \"{1}\", which is neither an IP address nor a CIDR block. Give a comma-separated " +
        "list of the reverse proxies in front of this server, or leave the setting unset to ignore " +
        "X-Forwarded-For entirely.",
        ConfigurationKey,
        entry);
}
