using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using IPNetwork = System.Net.IPNetwork;

namespace Prisstyrning.Security;

public static class TrustedProxyForwarding
{
    public const string ConfigurationKey = "Security:TrustedProxyNetworks";

    public static IServiceCollection AddTrustedProxyForwarding(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var configuredNetworks = ParseNetworks(configuration[ConfigurationKey]);
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            foreach (var network in configuredNetworks)
                options.KnownIPNetworks.Add(network);
        });

        return services;
    }

    private static IReadOnlyList<IPNetwork> ParseNetworks(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
            return Array.Empty<IPNetwork>();

        var result = new List<IPNetwork>();
        foreach (var value in configuredValue.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPNetwork.TryParse(value, out var network))
                throw new InvalidOperationException(
                    $"{ConfigurationKey} contains invalid CIDR network '{value}'.");

            result.Add(network);
        }

        return result;
    }
}
