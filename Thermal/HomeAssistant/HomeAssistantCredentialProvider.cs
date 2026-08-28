using Microsoft.Extensions.Options;

namespace Prisstyrning.Thermal.HomeAssistant;

public interface IHomeAssistantCredentialProvider
{
    string? GetTelemetryToken();
    string? GetControlToken();
    bool HasTelemetryToken { get; }
    bool HasControlToken { get; }
}

/// <summary>
/// Resolves the two Home Assistant identities without ever logging credential material.
/// A configured file is authoritative so a missing Docker secret cannot silently fall
/// back to an old environment variable.
/// </summary>
public sealed class HomeAssistantCredentialProvider : IHomeAssistantCredentialProvider
{
    private readonly HomeAssistantTelemetryOptions _telemetry;
    private readonly HomeAssistantControlOptions _control;

    public HomeAssistantCredentialProvider(
        IOptions<HomeAssistantTelemetryOptions> telemetry,
        IOptions<HomeAssistantControlOptions> control)
    {
        _telemetry = telemetry.Value;
        _control = control.Value;
    }

    public string? GetTelemetryToken() => Resolve(_telemetry.Token, _telemetry.TokenFile);
    public string? GetControlToken() => Resolve(_control.Token, _control.TokenFile);
    public bool HasTelemetryToken => GetTelemetryToken() is not null;
    public bool HasControlToken => GetControlToken() is not null;

    private static string? Resolve(string inlineToken, string tokenFile)
    {
        if (!string.IsNullOrWhiteSpace(tokenFile))
        {
            try
            {
                var value = File.ReadAllText(tokenFile).Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        var inline = inlineToken.Trim();
        return string.IsNullOrWhiteSpace(inline) ? null : inline;
    }
}
