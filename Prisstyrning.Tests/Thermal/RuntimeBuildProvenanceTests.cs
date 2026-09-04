using System.Reflection;
using System.Reflection.Emit;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class RuntimeBuildProvenanceTests
{
    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void FromRevision_NormalizesSupportedCommitIdentifiers(string input, string expected)
    {
        var build = RuntimeBuildProvenance.FromRevision(input);

        Assert.True(build.HasRevision);
        Assert.Equal(expected, build.Revision);
        Assert.Equal(expected, build.RequireRevision());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]
    [InlineData("0123456789abcdef0123456789abcdef012345678")]
    [InlineData("0123456789abcdef0123456789abcdef0123456g")]
    [InlineData("0000000000000000000000000000000000000000")]
    public void FromRevision_FailsClosedForMissingAmbiguousOrInvalidValues(string? input)
    {
        var build = RuntimeBuildProvenance.FromRevision(input);

        Assert.False(build.HasRevision);
        Assert.Null(build.Revision);
        Assert.Throws<InvalidOperationException>(build.RequireRevision);
    }

    [Fact]
    public void FromAssembly_RequiresExactlyOneValidMetadataValue()
    {
        var valid = AssemblyWithMetadata(ThermalCurrentModelTestData.BuildRevision);
        var duplicate = AssemblyWithMetadata(ThermalCurrentModelTestData.BuildRevision, ThermalCurrentModelTestData.BuildRevision);
        var malformed = AssemblyWithMetadata("mutable-tag");

        Assert.Equal(ThermalCurrentModelTestData.BuildRevision, RuntimeBuildProvenance.FromAssembly(valid).Revision);
        Assert.False(RuntimeBuildProvenance.FromAssembly(duplicate).HasRevision);
        Assert.False(RuntimeBuildProvenance.FromAssembly(malformed).HasRevision);
        Assert.False(RuntimeBuildProvenance.FromAssembly(AssemblyWithMetadata()).HasRevision);
    }

    private static Assembly AssemblyWithMetadata(params string[] values)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"PrisstyrningBuildEvidence{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        foreach (var value in values)
            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                constructor,
                [RuntimeBuildProvenance.AssemblyMetadataKey, value]));
        return assembly;
    }
}
