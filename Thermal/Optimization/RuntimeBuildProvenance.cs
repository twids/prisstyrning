using System.Reflection;

namespace Prisstyrning.Thermal.Optimization;

/// <summary>
/// Source identity embedded in the binary, not a runtime configuration value.
/// Image signature and digest verification remain separate release checks.
/// </summary>
public sealed class RuntimeBuildProvenance
{
    internal const string AssemblyMetadataKey = "Prisstyrning.SourceRevision";

    private RuntimeBuildProvenance(string? revision)
    {
        Revision = Normalize(revision);
    }

    public string? Revision { get; }
    public bool HasRevision => Revision is not null;

    internal string RequireRevision() => Revision ?? throw new InvalidOperationException(
            "Den körande programversionen saknar en inbakad källkodsrevision. Modellträning är spärrad tills en revisionsmärkt build används.");

    internal static RuntimeBuildProvenance FromAssembly(Assembly assembly) => new(ReadRevision(assembly));

    internal static RuntimeBuildProvenance FromRevision(string? revision) => new(revision);

    internal static string? Normalize(string? revision)
    {
        if (string.IsNullOrWhiteSpace(revision)) return null;
        var normalized = revision.Trim().ToLowerInvariant();
        if (normalized.Length is not (40 or 64) || normalized.All(character => character == '0') ||
            normalized.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return null;
        return normalized;
    }

    internal static string? ReadRevision(Assembly assembly)
    {
        var values = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => string.Equals(attribute.Key, AssemblyMetadataKey, StringComparison.Ordinal))
            .Select(attribute => Normalize(attribute.Value))
            .ToArray();
        return values.Length == 1 ? values[0] : null;
    }
}
