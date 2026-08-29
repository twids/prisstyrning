using Microsoft.AspNetCore.DataProtection;

namespace Prisstyrning.Tests.Unit;

public sealed class UserSessionCookieServiceTests
{
    [Fact]
    public void Protect_RoundTripsAValidUserId()
    {
        var service = new UserSessionCookieService(new EphemeralDataProtectionProvider());
        var protectedValue = service.Protect("user-12345678");

        Assert.StartsWith("v1.", protectedValue, StringComparison.Ordinal);
        Assert.True(service.TryUnprotect(protectedValue, out var userId));
        Assert.Equal("user-12345678", userId);
    }

    [Fact]
    public void TryUnprotect_RejectsUnsignedAndTamperedValues()
    {
        var service = new UserSessionCookieService(new EphemeralDataProtectionProvider());
        var protectedValue = service.Protect("user-12345678");
        var tamperedCharacters = protectedValue.ToCharArray();
        var tamperedIndex = protectedValue.Length / 2;
        tamperedCharacters[tamperedIndex] = tamperedCharacters[tamperedIndex] == 'A' ? 'B' : 'A';
        var tampered = new string(tamperedCharacters);

        Assert.False(service.TryUnprotect("user-12345678", out _));
        Assert.False(service.TryUnprotect(tampered, out _));
    }

    [Fact]
    public void Protect_RejectsInvalidUserIds()
    {
        var service = new UserSessionCookieService(new EphemeralDataProtectionProvider());
        Assert.Throws<ArgumentException>(() => service.Protect("../another-user"));
    }
}
