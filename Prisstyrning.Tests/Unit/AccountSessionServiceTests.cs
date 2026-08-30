using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Security;

namespace Prisstyrning.Tests.Unit;

public sealed class AccountSessionServiceTests
{
    [Fact]
    public async Task SignIn_CreatesSignedDatabaseSessionWithoutPersistingTheRawSubject()
    {
        await using var db = Database();
        var authentication = new RecordingAuthenticationService();
        var context = Context(authentication);
        var service = new AccountSessionService(db, new ConfigurationBuilder().Build());

        await service.SignInAsync(context, "daikin-account-a", "verified-daikin-subject");

        var account = await db.UserAccounts.SingleAsync();
        var session = await db.UserSessions.SingleAsync();
        Assert.Equal("daikin-account-a", account.UserId);
        Assert.NotEqual("verified-daikin-subject", account.DaikinSubjectHash);
        Assert.Equal(64, account.DaikinSubjectHash.Length);
        Assert.Equal(account.UserId, session.UserId);
        Assert.Equal(account.UserId, AccountAuthentication.UserId(authentication.Principal!));
        Assert.Equal(AccountAuthentication.Scheme, authentication.Scheme);
    }

    [Fact]
    public async Task SignIn_RejectsAnotherVerifiedSubjectForAnExistingAccount()
    {
        await using var db = Database();
        var authentication = new RecordingAuthenticationService();
        var service = new AccountSessionService(db, new ConfigurationBuilder().Build());
        await service.SignInAsync(Context(authentication), "daikin-account-a", "subject-a");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SignInAsync(Context(authentication), "daikin-account-a", "subject-b"));

        Assert.Single(await db.UserSessions.ToListAsync());
    }

    [Fact]
    public async Task SignOut_RevokesThePersistentSessionAndDeletesTheCookieTicket()
    {
        await using var db = Database();
        var authentication = new RecordingAuthenticationService();
        var service = new AccountSessionService(db, new ConfigurationBuilder().Build());
        var context = Context(authentication);
        await service.SignInAsync(context, "daikin-account-a", "subject-a");
        context.User = authentication.Principal!;

        await service.SignOutAsync(context);

        Assert.NotNull((await db.UserSessions.SingleAsync()).RevokedAtUtc);
        Assert.True(authentication.SignedOut);
    }

    private static PrisstyrningDbContext Database() => new(
        new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase($"account-session-{Guid.NewGuid():N}")
            .Options);

    private static DefaultHttpContext Context(IAuthenticationService authentication)
    {
        var services = new ServiceCollection()
            .AddSingleton(authentication)
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        return new DefaultHttpContext { RequestServices = services };
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public string? Scheme { get; private set; }
        public System.Security.Claims.ClaimsPrincipal? Principal { get; private set; }
        public bool SignedOut { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, System.Security.Claims.ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            Scheme = scheme;
            Principal = principal;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOut = true;
            return Task.CompletedTask;
        }
    }
}
