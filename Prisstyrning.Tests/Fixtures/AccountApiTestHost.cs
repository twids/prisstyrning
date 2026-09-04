using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prisstyrning.Data;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Security;
using Prisstyrning.Tests.Thermal;
using Prisstyrning.Thermal;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Fixtures;

/// <summary>
/// HTTP transport, cookies, account validation, API/CSRF guards and admin handlers
/// are real. Storage and keys are isolated. Selected read-only thermal/HA catalog
/// routes can be mapped explicitly. Program, migrations, Hangfire, HA/EMHASS/
/// Daikin integration clients and their workers are never registered or started.
/// </summary>
internal sealed class AccountApiTestHost : IAsyncDisposable
{
    private readonly TempFileSystem _files = new();
    private readonly string _databaseName = $"account-http-{Guid.NewGuid():N}";
    private IHost? _host;
    private int _mutationCount;

    public IConfiguration Configuration { get; }
    public IServiceProvider Services => _host!.Services;
    public int MutationCount => Volatile.Read(ref _mutationCount);

    private AccountApiTestHost(Dictionary<string, string?>? configuration)
    {
        Configuration = _files.GetTestConfig(configuration);
    }

    public static async Task<AccountApiTestHost> CreateAsync(
        Dictionary<string, string?>? configuration = null,
        bool includeThermalStatus = false,
        bool includeHomeAssistantEntities = false)
    {
        var fixture = new AccountApiTestHost(configuration);
        try
        {
            fixture._host = await new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseEnvironment(Environments.Production)
                    .UseTestServer()
                    .ConfigureAppConfiguration((_, builder) =>
                    {
                        builder.Sources.Clear();
                        builder.AddConfiguration(fixture.Configuration);
                    })
                    .ConfigureServices(services =>
                    {
                        services.AddLogging(logging => logging.ClearProviders());
                        services.AddRouting();
                        services.AddDataProtection().UseEphemeralDataProtectionProvider();
                        services.AddDbContext<PrisstyrningDbContext>(options =>
                            options.UseInMemoryDatabase(fixture._databaseName));
                        services.AddTestCredentialProtection();
                        services.AddScoped<UserSettingsRepository>();
                        services.AddScoped<DaikinTokenRepository>();
                        services.AddScoped<ScheduleHistoryRepository>();
                        services.AddTrustedProxyForwarding(fixture.Configuration);
                        services.AddAccountSessions();
                        services.AddAccountAntiforgery();
                        services.AddAdminLoginRateLimiting();
                        if (includeThermalStatus)
                        {
                            services.AddScoped<ThermalInstallationRegistry>();
                            services.AddSingleton<EmhassHealthState>();
                            services.AddSingleton(RuntimeBuildProvenance.FromRevision(
                                ThermalCurrentModelTestData.BuildRevision));
                        }
                        if (includeHomeAssistantEntities)
                        {
                            services.AddSingleton<IHomeAssistantStateCache, HomeAssistantStateCache>();
                            services.AddSingleton<HomeAssistantConnectionChanges>();
                            services.AddScoped<HomeAssistantConnectionService>();
                            services.AddSingleton<IHomeAssistantEndpointValidator, NoNetworkEndpointValidator>();
                        }
                    })
                    .Configure(app =>
                    {
                        app.UseForwardedHeaders();
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseRateLimiter();
                        app.UseAccountApiSecurity();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapAccountSessionEndpoints();
                            endpoints.MapAdminEndpoints();
                            if (includeThermalStatus) endpoints.MapThermalStatusApi();
                            if (includeHomeAssistantEntities) endpoints.MapHomeAssistantEntityCatalogApi();

                            // Synthetic identity entry exists ONLY in the test assembly.
                            // No production login/OAuth endpoint is bypassed or replaced.
                            endpoints.MapPost("/_test/sign-in/{userId}", async (
                                string userId, HttpContext context, AccountSessionService sessions) =>
                            {
                                await sessions.SignInAsync(context, userId, $"test-subject-{userId}");
                                return Results.NoContent();
                            });
                            endpoints.MapGet("/api/test/account", (HttpContext context) =>
                                Results.Json(new { userId = AccountAuthentication.UserId(context.User) }));
                            endpoints.MapGet("/api/test/administrator", () => Results.NoContent())
                                .RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });
                            foreach (var path in new[] { "/api/test/mutation", "/auth/daikin/test-action" })
                            {
                                endpoints.MapMethods(path, new[] { "POST", "PUT", "PATCH", "DELETE" }, () =>
                                {
                                    Interlocked.Increment(ref fixture._mutationCount);
                                    return Results.NoContent();
                                });
                            }
                            // Sentinel handlers test which paths are public, not the OAuth exchange.
                            endpoints.MapGet("/auth/daikin/start", () => Results.NoContent());
                            endpoints.MapGet("/auth/daikin/callback", () => Results.NoContent());
                        });
                    }))
                .StartAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public AccountTestBrowser CreateBrowser() => new(_host!.GetTestServer());

    public async Task WithServicesAsync(Func<IServiceProvider, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        _files.Dispose();
    }

    private sealed class NoNetworkEndpointValidator : IHomeAssistantEndpointValidator
    {
        public Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Read-only catalog tests must not resolve an external connection.");
    }
}

internal sealed class AccountTestBrowser : IDisposable
{
    private readonly CookieContainer _cookies = new();
    public HttpClient Client { get; }
    public string? CsrfToken { get; private set; }
    public string CookieHeader => _cookies.GetCookieHeader(Client.BaseAddress!);

    public AccountTestBrowser(TestServer server)
    {
        Client = new HttpClient(new CookieHandler(_cookies, server.CreateHandler()))
        {
            BaseAddress = new Uri("https://daikin.example.test")
        };
    }

    public async Task SignInAsync(string userId = "account-a")
    {
        using var response = await Client.PostAsync($"/_test/sign-in/{Uri.EscapeDataString(userId)}", null);
        response.EnsureSuccessStatusCode();
        await ReadSessionAsync();
    }

    public async Task<TestSessionStatus> ReadSessionAsync()
    {
        using var response = await Client.GetAsync("/api/session");
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<TestSessionStatus>()
            ?? throw new InvalidOperationException("Missing test session response.");
        CsrfToken = session.CsrfToken;
        return session;
    }

    public async Task<HttpResponseMessage> MutateAsync(
        HttpMethod method,
        string path,
        string? csrfToken = null,
        string? adminPassword = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (csrfToken is not null) request.Headers.Add(AccountAntiforgery.HeaderName, csrfToken);
        if (adminPassword is not null) request.Headers.Add("X-Admin-Password", adminPassword);
        return await Client.SendAsync(request);
    }

    public void Dispose() => Client.Dispose();

    private sealed class CookieHandler(CookieContainer cookies, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.Contains("Cookie"))
            {
                var cookieHeader = cookies.GetCookieHeader(request.RequestUri!);
                if (!string.IsNullOrEmpty(cookieHeader)) request.Headers.Add("Cookie", cookieHeader);
            }
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values) cookies.SetCookies(request.RequestUri!, value);
            return response;
        }
    }
}

internal sealed record TestSessionStatus(bool Authenticated, string? UserId, bool IsAdmin, string? CsrfToken);
