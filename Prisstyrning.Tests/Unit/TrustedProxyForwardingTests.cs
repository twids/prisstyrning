using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Prisstyrning.Security;
using IPNetwork = System.Net.IPNetwork;

namespace Prisstyrning.Tests.Unit;

public sealed class TrustedProxyForwardingTests
{
    [Fact]
    public void AddTrustedProxyForwarding_ConfiguresOnlyRequiredHeadersAndOneProxyHop()
    {
        var configuration = Configuration("172.19.0.0/16, 10.42.0.8/32");
        var services = new ServiceCollection();

        services.AddTrustedProxyForwarding(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Contains(IPNetwork.Parse("172.19.0.0/16"), options.KnownIPNetworks);
        Assert.Contains(IPNetwork.Parse("10.42.0.8/32"), options.KnownIPNetworks);
    }

    [Fact]
    public void AddTrustedProxyForwarding_WithoutConfiguredNetwork_KeepsFrameworkTrustBoundary()
    {
        var services = new ServiceCollection();

        services.AddTrustedProxyForwarding(Configuration(null));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.NotEmpty(options.KnownIPNetworks);
        Assert.DoesNotContain(IPNetwork.Parse("172.19.0.0/16"), options.KnownIPNetworks);
    }

    [Fact]
    public void AddTrustedProxyForwarding_WithInvalidNetwork_FailsClosed()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddTrustedProxyForwarding(Configuration("all-proxies")));

        Assert.Contains(TrustedProxyForwarding.ConfigurationKey, exception.Message);
    }

    private static IConfiguration Configuration(string? networks)
    {
        var values = networks is null
            ? Array.Empty<KeyValuePair<string, string?>>()
            : new[] { new KeyValuePair<string, string?>(TrustedProxyForwarding.ConfigurationKey, networks) };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
