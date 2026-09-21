using System.Net;
using Microsoft.Extensions.Configuration;
using WorkLedger.Server;

namespace WorkLedger.Server.Tests;

/// <summary>
/// Parsing and validation of <c>Network:TrustedProxies</c>, with no host and no database — the
/// behaviour it drives is covered by <c>ProxyRateLimitTests</c>.
/// <para>
/// The failure cases matter more than the happy ones here. A silently skipped entry would leave an
/// operator believing they had configured the proxy they just misspelled, and the symptom is identical
/// to not having configured one at all.
/// </para>
/// </summary>
public class TrustedProxiesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnsetValue_MeansNotConfigured(string? configured)
    {
        var trustedProxies = Resolve(configured);

        Assert.False(trustedProxies.IsConfigured);
        Assert.Empty(trustedProxies.Addresses);
        Assert.Empty(trustedProxies.Networks);
        Assert.Equal("(none)", trustedProxies.Describe());
    }

    [Fact]
    public void AddressesAndNetworks_AreSortedIntoTheirOwnLists()
    {
        var trustedProxies = Resolve("10.0.0.7, 10.1.0.0/16, ::1, 2001:db8::/32");

        Assert.Equal(
            [IPAddress.Parse("10.0.0.7"), IPAddress.Parse("::1")],
            trustedProxies.Addresses);
        Assert.Equal(
            [IPNetwork.Parse("10.1.0.0/16"), IPNetwork.Parse("2001:db8::/32")],
            trustedProxies.Networks);
        Assert.True(trustedProxies.IsConfigured);
    }

    /// <summary>Compose renders a list with whatever spacing the operator typed; empty entries are not an error.</summary>
    [Fact]
    public void WhitespaceAndEmptyEntries_AreTolerated()
    {
        var trustedProxies = Resolve("  10.0.0.7 ,, 10.0.0.8,  ");

        Assert.Equal(
            [IPAddress.Parse("10.0.0.7"), IPAddress.Parse("10.0.0.8")],
            trustedProxies.Addresses);
    }

    /// <summary>
    /// A typo fails startup rather than being dropped. The message has to name the offending value —
    /// it is the only thing an operator reading <c>docker compose logs</c> has to go on.
    /// </summary>
    [Fact]
    public void AnEntryThatIsNeitherAnAddressNorABlock_ThrowsNamingIt()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Resolve("10.0.0.7, not-an-address"));

        Assert.Contains("not-an-address", exception.Message);
        Assert.Contains(TrustedProxies.ConfigurationKey, exception.Message);
    }

    /// <summary>
    /// Host bits set are <b>not</b> an error — <c>IPNetwork.Parse</c> normalizes them away and matching
    /// then does what the operator meant. Pinned because rejecting this input is a plausible-looking
    /// "fix" that would refuse a configuration which already works.
    /// </summary>
    [Fact]
    public void ACidrBlockWithHostBitsSet_IsNormalizedRatherThanRejected()
    {
        var network = Assert.Single(Resolve("10.0.0.5/8").Networks);

        Assert.Equal(IPNetwork.Parse("10.0.0.0/8"), network);
        Assert.True(network.Contains(IPAddress.Parse("10.9.9.9")));
        Assert.False(network.Contains(IPAddress.Parse("11.0.0.1")));
    }

    /// <summary>A block that is genuinely malformed still fails startup, naming the value.</summary>
    [Fact]
    public void ACidrBlockWithAnImpossiblePrefixLength_ThrowsNamingIt()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Resolve("10.0.0.0/99"));

        Assert.Contains("10.0.0.0/99", exception.Message);
    }

    [Fact]
    public void Describe_ListsEverythingConfigured_ForTheStartupLine()
    {
        Assert.Equal("10.0.0.7, 10.1.0.0/16", Resolve("10.0.0.7, 10.1.0.0/16").Describe());
    }

    private static TrustedProxies Resolve(string? configured) =>
        TrustedProxies.Resolve(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TrustedProxies.ConfigurationKey] = configured,
            })
            .Build());
}
