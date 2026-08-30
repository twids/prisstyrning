using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Prisstyrning.Security;

namespace Prisstyrning.Tests.Fixtures;

internal static class TestSecretProtector
{
    internal static IAccountSecretProtector Instance { get; } =
        new AccountSecretProtector(Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray());

    internal static IServiceCollection AddTestCredentialProtection(this IServiceCollection services)
    {
        services.AddSingleton(Instance);
        services.AddSingleton(Options.Create(new CredentialEncryptionOptions
        {
            PreserveLegacyDaikinTokenColumns = false
        }));
        return services;
    }
}
